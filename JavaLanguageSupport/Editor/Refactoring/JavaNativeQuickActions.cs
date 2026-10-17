using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;

namespace JavaLanguageSupport.Editor.Refactoring
{
    /// <summary>
    /// Bridges JDT LS textDocument/codeAction into Visual Studio's native
    /// light-bulb menu. This provider is exclusive to the Java content type.
    /// No interception of Ctrl+. and no extra keyboard binding are needed.
    /// </summary>
    [Export(typeof(ISuggestedActionsSourceProvider))]
    [Name("Java JDT LS Native Quick Actions")]
    [ContentType("java")]
    [Order(After = DefaultOrderings.Default)]
    [SuggestedActionPriority(DefaultOrderings.Default)]
    internal sealed class JavaNativeQuickActionsProvider : ISuggestedActionsSourceProvider
    {
        [Import] internal ITextDocumentFactoryService Documents = null;

        public ISuggestedActionsSource CreateSuggestedActionsSource(ITextView view, ITextBuffer buffer)
        {
            if (Documents == null || view == null || buffer == null ||
                !ReferenceEquals(view.TextBuffer, buffer) ||
                !buffer.ContentType.IsOfType("java"))
                return null;
            return new JavaNativeQuickActionsSource(view, buffer, Documents);
        }
    }

    internal sealed class JavaNativeQuickActionsSource : IAsyncSuggestedActionsSource
    {
        private readonly ITextView view;
        private readonly ITextBuffer buffer;
        private readonly ITextDocumentFactoryService documents;
        private bool disposed;

        internal JavaNativeQuickActionsSource(ITextView view, ITextBuffer buffer,
            ITextDocumentFactoryService documents)
        {
            this.view = view;
            this.buffer = buffer;
            this.documents = documents;
        }

        // VS requests this during cursor movement. Do not call JDT LS here.
        public Task<bool> HasSuggestedActionsAsync(ISuggestedActionCategorySet categories,
            SnapshotSpan range, CancellationToken token)
        {
            return Task.FromResult(!disposed && JavaLspRpc.Connected);
        }

        public Task<ISuggestedActionCategorySet> GetSuggestedActionCategoriesAsync(
            ISuggestedActionCategorySet categories, SnapshotSpan range, CancellationToken token)
        {
            return Task.FromResult(!disposed && JavaLspRpc.Connected ? categories : null);
        }

        // Legacy path: do not synchronously wait for JSON-RPC on the UI thread.
        public IEnumerable<SuggestedActionSet> GetSuggestedActions(
            ISuggestedActionCategorySet categories, SnapshotSpan range, CancellationToken token)
        {
            return Enumerable.Empty<SuggestedActionSet>();
        }

        public async Task GetSuggestedActionsAsync(ISuggestedActionCategorySet categories,
            SnapshotSpan range, ImmutableArray<ISuggestedActionSetCollector> collectors,
            CancellationToken token)
        {
            // Every collector MUST be completed, even on timeout, cancellation or errors.
            foreach (var collector in collectors)
            {
                try
                {
                    if (disposed || !JavaLspRpc.Connected) continue;
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                    if (disposed || view.IsClosed) continue;
                    ITextDocument document;
                    if (!documents.TryGetTextDocument(buffer, out document)) continue;
                    string uri = JavaLspRpc.FileUri(document);
                    if (string.IsNullOrWhiteSpace(uri)) continue;

                    ITextSnapshot snapshot = buffer.CurrentSnapshot;
                    int caret = Math.Min(view.Caret.Position.BufferPosition.Position,
                        snapshot.Length);
                    ITextSnapshotLine row = snapshot.GetLineFromPosition(caret);
                    int character = caret - row.Start.Position;
                    JArray diagnostics = JavaDiagnosticsMonitor.ForPosition(uri,
                        row.LineNumber, character);

                    // Ctrl+. on a selection must send the WHOLE range to JDT LS.
                    // A caret-only or diagnostic-only range cannot extract code.
                    // Do not change the selection or capture a stale snapshot.
                    SnapshotSpan selectedRange = new SnapshotSpan(snapshot, caret, 0);
                    var selectedSpans = view.Selection.SelectedSpans;
                    if (!view.Selection.IsEmpty && selectedSpans.Count == 1 &&
                        selectedSpans[0].Snapshot == snapshot &&
                        selectedSpans[0].Snapshot.TextBuffer == buffer)
                        selectedRange = selectedSpans[0];
                    object lspRange = !selectedRange.IsEmpty
                        ? (object)new {
                            start = JavaLspRpc.Position(snapshot, selectedRange.Start.Position),
                            end = JavaLspRpc.Position(snapshot, selectedRange.End.Position) }
                        : diagnostics.Count != 0 && diagnostics[0]["range"] != null
                        ? (object)diagnostics[0]["range"].DeepClone()
                        : new { start = JavaLspRpc.Position(snapshot, caret),
                                end = JavaLspRpc.Position(snapshot, caret) };
                    var request = new
                    {
                        textDocument = new { uri },
                        range = lspRange,
                        context = new { diagnostics }
                    };
                    JToken response = await JavaLspRpc.RequestAsync(
                        "textDocument/codeAction", request, token).ConfigureAwait(false);
                    JArray items = response as JArray;
                    if (items == null || items.Count == 0) continue;

                    // Do not display fixes for stale buffers. LSP edits are bound
                    // to the exact snapshot used to query the server.
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                    if (disposed || view.IsClosed ||
                        !ReferenceEquals(snapshot, buffer.CurrentSnapshot)) continue;

                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    var actions = new List<ISuggestedAction>();
                    foreach (JObject item in items.OfType<JObject>().Take(
                        selectedRange.IsEmpty ? 60 : 120))
                    {
                        string title = (string)item["title"];
                        if (string.IsNullOrWhiteSpace(title) || !seen.Add(title)) continue;
                        actions.Add(new JavaNativeSuggestedAction(title, item.DeepClone() as JObject,
                            buffer, snapshot, uri));
                    }
                    if (actions.Count != 0)
                        collector.Add(new SuggestedActionSet(
                            PredefinedSuggestedActionCategoryNames.CodeFix, actions));
                }
                catch (OperationCanceledException) { /* The native menu was dismissed. */ }
                catch (Exception ex)
                {
                    JavaLanguageStatus.LspFailure("Java/nativeCodeAction", ex.Message);
                }
                finally { collector.Complete(); }
            }
        }

        public event EventHandler<EventArgs> SuggestedActionsChanged
        {
            add { }
            remove { }
        }

        public bool TryGetTelemetryId(out Guid telemetryId)
        {
            telemetryId = Guid.Empty;
            return false;
        }

        public void Dispose() { disposed = true; }
    }

    internal sealed class JavaNativeSuggestedAction : ISuggestedAction
    {
        private readonly string title;
        private readonly JObject action;
        private readonly ITextBuffer buffer;
        private readonly ITextSnapshot snapshot;
        private readonly string uri;

        internal JavaNativeSuggestedAction(string title, JObject action,
            ITextBuffer buffer, ITextSnapshot snapshot, string uri)
        {
            this.title = title;
            this.action = action;
            this.buffer = buffer;
            this.snapshot = snapshot;
            this.uri = uri;
        }

        public string DisplayText { get { return title; } }
        public bool HasActionSets { get { return false; } }
        public bool HasPreview { get { return false; } }
        public string IconAutomationText { get { return "Correzione Java"; } }
        public ImageMoniker IconMoniker { get { return default(ImageMoniker); } }
        public string InputGestureText { get { return ""; } }

        public Task<IEnumerable<SuggestedActionSet>> GetActionSetsAsync(CancellationToken token)
        {
            return Task.FromResult<IEnumerable<SuggestedActionSet>>(
                Enumerable.Empty<SuggestedActionSet>());
        }

        public Task<object> GetPreviewAsync(CancellationToken token)
        {
            return Task.FromResult<object>(null);
        }

        public void Invoke(CancellationToken token)
        {
            // VS invokes synchronously. Never block its UI thread waiting for
            // codeAction/resolve; dispatch asynchronously to the existing editor logic.
            ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                try
                {
                    await JavaEditorLspOperations.ApplySuggestedCodeActionAsync(
                        action, buffer, snapshot, uri,
                        line => System.Diagnostics.Trace.WriteLine("[Java Quick Fix] " + line),
                        token);
                }
                catch (Exception ex)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    System.Windows.MessageBox.Show("Correzione Java non riuscita:\n" + ex.Message,
                        "Java: Azioni rapide", System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                }
            }).FileAndForget("JavaLanguageSupport/NativeQuickFix");
        }

        public bool TryGetTelemetryId(out Guid telemetryId)
        {
            telemetryId = Guid.Empty;
            return false;
        }

        public void Dispose() { }
    }
}
