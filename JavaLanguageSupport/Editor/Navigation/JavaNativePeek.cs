using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json.Linq;

namespace JavaLanguageSupport.Editor.Navigation
{
    // Pack 8: VS built-in inline Peek. Do not replace the Java editor, open another
    // JDT LS process, or change the existing F12 and Ctrl+click navigation.
    [Export(typeof(IPeekableItemSourceProvider))]
    [Name("Java JDT LS Native Peek Definition")]
    [ContentType("java")]
    [SupportsStandaloneFiles(true)]
    [SupportsPeekRelationship("IsDefinedBy")]
    internal sealed class JavaNativePeekSourceProvider : IPeekableItemSourceProvider
    {
        [Import] internal ITextDocumentFactoryService Documents = null;
        [Import] internal IPeekResultFactory ResultFactory = null;

        public IPeekableItemSource TryCreatePeekableItemSource(ITextBuffer buffer)
        {
            if (buffer == null || !buffer.ContentType.IsOfType("java") ||
                Documents == null || ResultFactory == null)
                return null;
            return new JavaNativePeekSource(buffer, Documents, ResultFactory);
        }
    }

    internal sealed class JavaNativePeekSource : IPeekableItemSource
    {
        private readonly ITextBuffer buffer;
        private readonly ITextDocumentFactoryService documents;
        private readonly IPeekResultFactory factory;
        private bool disposed;

        internal JavaNativePeekSource(ITextBuffer buffer,
            ITextDocumentFactoryService documents, IPeekResultFactory factory)
        {
            this.buffer = buffer;
            this.documents = documents;
            this.factory = factory;
        }

        public void AugmentPeekSession(IPeekSession session,
            IList<IPeekableItem> peekableItems)
        {
            if (disposed || session == null || peekableItems == null ||
                !JavaLspRpc.Connected ||
                !string.Equals(session.RelationshipName,
                    PredefinedPeekRelationships.Definitions.Name,
                    StringComparison.OrdinalIgnoreCase))
                return;

            ITextDocument document;
            if (!documents.TryGetTextDocument(buffer, out document)) return;
            string uri = JavaLspRpc.FileUri(document);
            if (string.IsNullOrEmpty(uri)) return;
            ITextSnapshot snapshot = buffer.CurrentSnapshot;
            SnapshotPoint? trigger = session.GetTriggerPoint(snapshot);
            if (!trigger.HasValue) return;
            int offset = trigger.Value.Position;
            if (offset < 0 || offset > snapshot.Length) return;

            // IMPORTANT: AugmentPeekSession is synchronous and may run on the
            // editor thread. Add only a cheap result source here. JSON-RPC takes
            // place later in IPeekResultSource.FindResults, not on every cursor move.
            peekableItems.Add(new JavaNativePeekableItem(uri, snapshot, offset, factory));
        }

        public void Dispose() { disposed = true; }
    }

    internal sealed class JavaNativePeekableItem : IPeekableItem
    {
        private readonly string uri;
        private readonly ITextSnapshot snapshot;
        private readonly int offset;
        private readonly IPeekResultFactory factory;

        internal JavaNativePeekableItem(string uri, ITextSnapshot snapshot,
            int offset, IPeekResultFactory factory)
        {
            this.uri = uri;
            this.snapshot = snapshot;
            this.offset = offset;
            this.factory = factory;
        }

        public string DisplayName { get { return "Definizione Java"; } }
        public IEnumerable<IPeekRelationship> Relationships
        {
            get { yield return PredefinedPeekRelationships.Definitions; }
        }

        public IPeekResultSource GetOrCreateResultSource(string relationshipName)
        {
            if (!string.Equals(relationshipName,
                PredefinedPeekRelationships.Definitions.Name,
                StringComparison.OrdinalIgnoreCase)) return null;
            return new JavaNativePeekResultSource(uri, snapshot, offset, factory);
        }
    }

    internal sealed class JavaNativePeekResultSource : IPeekResultSource
    {
        private readonly string uri;
        private readonly ITextSnapshot snapshot;
        private readonly int offset;
        private readonly IPeekResultFactory factory;

        internal JavaNativePeekResultSource(string uri, ITextSnapshot snapshot,
            int offset, IPeekResultFactory factory)
        {
            this.uri = uri;
            this.snapshot = snapshot;
            this.offset = offset;
            this.factory = factory;
        }

        public void FindResults(string relationshipName, IPeekResultCollection results,
            CancellationToken cancellationToken, IFindPeekResultsCallback callback)
        {
            if (results == null ||
                !string.Equals(relationshipName,
                    PredefinedPeekRelationships.Definitions.Name,
                    StringComparison.OrdinalIgnoreCase)) return;

            // VS performs Peek result resolution in its result-source phase,
            // rather than when the user navigates the editor. Bound requests to
            // avoid indefinitely pending the native inline Peek UI.
            using (var limited = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                limited.CancelAfter(TimeSpan.FromSeconds(8));
                try
                {
                    if (!JavaLspRpc.Connected ||
                        !ReferenceEquals(snapshot, snapshot.TextBuffer.CurrentSnapshot)) return;
                    var parameters = new
                    {
                        textDocument = new { uri },
                        position = JavaLspRpc.Position(snapshot, offset)
                    };
                    JToken reply = JavaLspRpc.RequestAsync("textDocument/definition",
                        parameters, limited.Token).GetAwaiter().GetResult();
                    limited.Token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(snapshot, snapshot.TextBuffer.CurrentSnapshot)) return;

                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (JObject location in JavaAdvancedNavigation.Objects(reply).Take(32))
                    {
                        limited.Token.ThrowIfCancellationRequested();
                        JavaAdvancedLocation target = JavaAdvancedNavigation.Location(location);
                        if (target == null) continue;
                        string unique = target.Uri + "|" + target.Line + "|" + target.Column;
                        if (!seen.Add(unique)) continue;

                        // Handles file:// as well as jdt:// (JDK/classpath source
                        // cached in a read-only .java file by the existing resolver).
                        string path = JavaNavigation.ResolvePathAsync(target.Uri,
                            message => Trace.WriteLine("[Java Native Peek] " + message),
                            limited.Token).GetAwaiter().GetResult();
                        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;

                        string label = Path.GetFileName(path) + " (" +
                            (target.Line + 1) + ", " + (target.Column + 1) + ")";
                        var info = new PeekResultDisplayInfo(
                            label: label, labelTooltip: path,
                            title: Path.GetFileName(path), titleTooltip: path);
                        bool readOnly = !new Uri(target.Uri).IsFile ||
                            (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
                        // Native VS document Peek takes zero-based locations.
                        // The display engine owns the inline editor, theming,
                        // resizing, result list and keyboard shortcuts.
                        results.Add(factory.Create(info, path,
                            target.Line, target.Column,
                            target.Line, target.Column,
                            target.Line, target.Column,
                            readOnly));
                    }
                }
                catch (OperationCanceledException) { /* Dismissed/timeout. */ }
                catch (Exception ex)
                {
                    JavaLanguageStatus.LspFailure("java/nativePeek",
                        ex.GetType().Name + ": " + ex.Message);
                    Trace.WriteLine("[Java Native Peek] " + ex);
                }
            }
        }
    }

    internal static class JavaNativePeek
    {
        // The extension's existing context-menu command reuses the same native
        // Alt+F12 Peek broker. If the broker is missing, the caller keeps its
        // previous, tested WPF preview as a fallback.
        internal static bool TryTrigger(ITextSnapshot snapshot, int offset, Action<string> log)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (snapshot == null || !JavaLspRpc.Connected) return false;
            try
            {
                var model = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
                var manager = Package.GetGlobalService(typeof(SVsTextManager)) as IVsTextManager;
                if (model == null || manager == null) return false;
                var broker = model.GetService<IPeekBroker>();
                var adapters = model.GetService<IVsEditorAdaptersFactoryService>();
                if (broker == null || adapters == null) return false;
                IVsTextView active;
                if (manager.GetActiveView(1, null, out active) < 0 || active == null)
                    return false;
                IWpfTextView view = adapters.GetWpfTextView(active);
                if (view == null || view.IsClosed ||
                    !ReferenceEquals(view.TextBuffer, snapshot.TextBuffer) ||
                    !view.TextBuffer.ContentType.IsOfType("java")) return false;
                int safe = Math.Max(0, Math.Min(offset, view.TextBuffer.CurrentSnapshot.Length));
                if (!ReferenceEquals(snapshot, view.TextBuffer.CurrentSnapshot)) return false;
                var point = view.TextBuffer.CurrentSnapshot.CreateTrackingPoint(safe,
                    PointTrackingMode.Positive);
                IPeekSession session = broker.TriggerPeekSession(view, point,
                    PredefinedPeekRelationships.Definitions.Name);
                if (session == null) return false;
                log("Java Peek: sessione nativa di Visual Studio avviata.");
                return true;
            }
            catch (Exception ex)
            {
                log("Java Peek nativo non disponibile: " + ex.Message);
                return false;
            }
        }
    }
}
