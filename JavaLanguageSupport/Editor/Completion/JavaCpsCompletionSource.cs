using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json.Linq;

namespace JavaLanguageSupport.Editor.Completion
{
    /// <summary>
    /// F2J.1: CPS-only Visual Studio async-completion bridge to the EXISTING JDT LS.
    /// Open File/Open Folder retain the native LSP completion behavior unchanged.
    /// This provides completion items, not a second LSP session or project refresh.
    /// </summary>
    [Export(typeof(IAsyncCompletionSourceProvider))]
    [Name("Java CPS JDT LS Async Completion")]
    [ContentType("java")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class JavaCpsCompletionSourceProvider : IAsyncCompletionSourceProvider
    {
        [Import] internal ITextDocumentFactoryService Documents = null;

        public IAsyncCompletionSource GetOrCreate(ITextView textView)
        {
            if (textView == null || Documents == null ||
                !textView.TextBuffer.ContentType.IsOfType("java")) return null;
            return textView.Properties.GetOrCreateSingletonProperty(
                () => new JavaCpsCompletionSource(textView, Documents));
        }
    }

    internal sealed class JavaCpsCompletionSource : IAsyncCompletionSource
    {
        private const int MaxItems = 250;
        private readonly ITextBuffer buffer;
        private readonly ITextDocumentFactoryService documents;
        private readonly JavaCpsCompletionDocumentSync documentSync;

        internal JavaCpsCompletionSource(ITextView view,
            ITextDocumentFactoryService documents)
        {
            buffer = view.TextBuffer;
            this.documents = documents;
            // One LSP text-document state per buffer, even if there are split views.
            documentSync = buffer.Properties.GetOrCreateSingletonProperty(
                () => new JavaCpsCompletionDocumentSync(buffer));
            documentSync.RegisterView(view);
        }

        private bool Eligible()
        {
            if (!JavaLspRpc.Connected || !JavaLanguageStatus.IsInitialized ||
                FeatureDisabled()) return false;
            ITextDocument document;
            return documents.TryGetTextDocument(buffer, out document) &&
                JavaCpsWorkspaceContext.IsLoadedJavaDocument(document.FilePath);
        }

        private static bool FeatureDisabled()
        {
            try
            {
                return File.Exists(Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                    "JavaLanguageSupportVS", "disable-cps-completion.flag"));
            }
            catch (Exception) { return true; }
        }

        private static bool Identifier(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == '$';
        }

        private static SnapshotSpan WordSpan(SnapshotPoint position)
        {
            ITextSnapshot snapshot = position.Snapshot;
            int end = position.Position;
            int start = end;
            while (start > 0 && Identifier(snapshot[start - 1])) --start;
            return new SnapshotSpan(snapshot, start, end - start);
        }

        public CompletionStartData InitializeCompletion(CompletionTrigger trigger,
            SnapshotPoint triggerLocation, CancellationToken token)
        {
            if (token.IsCancellationRequested || !Eligible())
                return CompletionStartData.DoesNotParticipateInCompletion;
            if (trigger.Reason == CompletionTriggerReason.Insertion &&
                !Identifier(trigger.Character) && trigger.Character != '.')
                return CompletionStartData.DoesNotParticipateInCompletion;
            return new CompletionStartData(CompletionParticipation.ProvidesItems,
                WordSpan(triggerLocation));
        }

        public bool TryGetApplicableToSpan(char typedChar, SnapshotPoint triggerLocation,
            out SnapshotSpan applicableToSpan, CancellationToken token)
        {
            applicableToSpan = default(SnapshotSpan);
            if (token.IsCancellationRequested || !Eligible() ||
                (!Identifier(typedChar) && typedChar != '.')) return false;
            applicableToSpan = WordSpan(triggerLocation);
            return true;
        }

        public Task<CompletionContext> GetCompletionContextAsync(
            IAsyncCompletionSession session, CompletionTrigger trigger,
            SnapshotPoint triggerLocation, SnapshotSpan applicableToSpan,
            CancellationToken token)
        {
            return FetchAsync(triggerLocation, trigger, token);
        }

        private async Task<CompletionContext> FetchAsync(
            SnapshotPoint triggerLocation, CompletionTrigger trigger, CancellationToken token)
        {
            var empty = new CompletionContext(ImmutableArray<CompletionItem>.Empty);
            if (token.IsCancellationRequested || !Eligible()) return empty;
            ITextDocument document;
            if (!documents.TryGetTextDocument(buffer, out document)) return empty;
            string uri = JavaLspRpc.FileUri(document);
            if (string.IsNullOrWhiteSpace(uri)) return empty;

            ITextSnapshot snapshot = triggerLocation.Snapshot;
            if (!ReferenceEquals(snapshot, buffer.CurrentSnapshot)) return empty;

            // F2J.1b: CPS starts the language server, but Visual Studio may not
            // register CPS files as LSP open documents. Complete the document
            // handoff BEFORE querying completion, otherwise JDT LS can use a
            // different source version and propose members of another type.
            // Explicit diagnostic opt-in: do NOT send didOpen/didChange if VS
            // might already own the document. Duplicate opens violate LSP.
            if (JavaCpsCompletionDocumentSync.Enabled &&
                !await documentSync.EnsureAsync(uri, snapshot, token)
                    .ConfigureAwait(false)) return empty;
            if (!ReferenceEquals(snapshot, buffer.CurrentSnapshot)) return empty;

            int point = Math.Max(0, Math.Min(triggerLocation.Position, snapshot.Length));
            ITextSnapshotLine line = snapshot.GetLineFromPosition(point);
            bool dotTrigger = trigger.Reason == CompletionTriggerReason.Insertion &&
                trigger.Character == '.';
            var request = new
            {
                textDocument = new { uri },
                position = new { line = line.LineNumber,
                    character = point - line.Start.Position },
                context = dotTrigger
                    ? (object)new { triggerKind = 2, triggerCharacter = "." }
                    : (object)new { triggerKind = 1 }
            };
            JToken response;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    response = await JavaLspRpc.RequestAsync(
                        "textDocument/completion", request, timeout.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return empty; }
            }
            if (token.IsCancellationRequested || response == null ||
                !ReferenceEquals(snapshot, buffer.CurrentSnapshot)) return empty;

            JArray candidates = response as JArray ??
                (response as JObject)?["items"] as JArray;
            if (candidates == null) return empty;
            var items = ImmutableArray.CreateBuilder<CompletionItem>();
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (JObject candidate in candidates.OfType<JObject>().Take(MaxItems * 2))
            {
                string label = candidate["label"]?.Type == JTokenType.String
                    ? (string)candidate["label"] : (string)candidate.SelectToken("label.label");
                if (string.IsNullOrWhiteSpace(label)) continue;
                string insert = (string)candidate["insertText"] ??
                    (string)candidate.SelectToken("textEdit.newText") ?? label;
                // A plain VS completion item cannot safely apply additional
                // edits to imports; omit those entries rather than corrupting code.
                JArray extraEdits = candidate["additionalTextEdits"] as JArray;
                if (extraEdits != null && extraEdits.Count != 0) continue;
                if ((int?)candidate["insertTextFormat"] == 2)
                {
                    insert = PlainSnippet(insert);
                    if (insert == null) continue;
                }
                if (insert.IndexOf('\r') >= 0 || insert.IndexOf('\n') >= 0) continue;
                string key = label + "\n" + insert;
                if (!unique.Add(key)) continue;
                string sort = (string)candidate["sortText"] ?? label;
                string filter = (string)candidate["filterText"] ?? label;
                items.Add(new CompletionItem(label, this, default(ImageElement),
                    ImmutableArray<CompletionFilter>.Empty, string.Empty,
                    insert, sort, filter, ImmutableArray<ImageElement>.Empty));
                if (items.Count >= MaxItems) break;
            }
            return new CompletionContext(items.ToImmutable());
        }

        // Replace basic LSP tabstops with their default text. Complex nested
        // snippet constructs are deliberately skipped to avoid broken Java.
        private static string PlainSnippet(string input)
        {
            if (string.IsNullOrEmpty(input) || input.Length > 4096) return null;
            string result = Regex.Replace(input, @"\$\{[0-9]+:([^{}]*)\}", "$1");
            result = Regex.Replace(result, @"\$\{[0-9]+\|([^|{}]*)\|\}",
                match => match.Groups[1].Value.Split(',')[0]);
            result = Regex.Replace(result, @"\$\{[0-9]+\}|\$[0-9]+", "");
            return result.Contains("${") || Regex.IsMatch(result, @"\$[0-9]")
                ? null : result;
        }

        public Task<object> GetDescriptionAsync(IAsyncCompletionSession session,
            CompletionItem item, CancellationToken token)
        {
            return GetDescriptionAsync(item, token);
        }

        public Task<object> GetDescriptionAsync(CompletionItem item,
            CancellationToken token)
        {
            return Task.FromResult<object>(item == null ? null : item.DisplayText);
        }
    }

    /// <summary>
    /// F2J.1b: minimal CPS-only document lifecycle, shared by split views.
    /// This is deliberately scoped to the CPS completion fallback: normal
    /// Open File/Open Folder documents are never passed to EnsureAsync.
    /// Each request synchronizes the exact snapshot used for its LSP position.
    /// We do not send duplicate didOpen messages on every Ctrl+J.
    /// </summary>
    internal sealed class JavaCpsCompletionDocumentSync
    {
        // After the F2J.1b acceptance test this CPS-only bridge is enabled
        // normally. A kill switch lets the user revert immediately to the
        // former behavior without altering the VSIX or deleting JDT LS data.
        internal static bool Enabled
        {
            get
            {
                try
                {
                    return !File.Exists(Path.Combine(Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                        "JavaLanguageSupportVS", "disable-cps-document-sync.flag"));
                }
                catch (Exception) { return false; }
            }
        }

        private readonly ITextBuffer buffer;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private object sessionIdentity;
        private string openedUri;
        private int lastSnapshotVersion = -1;
        private int documentVersion;
        private int openViews;
        private int changeGeneration;
        // Creation listener and completion provider may register the SAME view.
        private readonly HashSet<ITextView> registeredViews = new HashSet<ITextView>();

        internal JavaCpsCompletionDocumentSync(ITextBuffer buffer)
        {
            this.buffer = buffer;
            buffer.Changed += OnBufferChanged;
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs change)
        {
            // The CPS document becomes owned by this bridge only after didOpen.
            // Debounce ordinary typing, and never run a semantic query here.
            if (!Enabled) return;
            string target = openedUri;
            if (string.IsNullOrWhiteSpace(target)) return;
            int generation = Interlocked.Increment(ref changeGeneration);
            var ignored = SynchronizeAfterEditAsync(target, change.After, generation);
        }

        private async Task SynchronizeAfterEditAsync(string uri, ITextSnapshot snapshot,
            int generation)
        {
            try
            {
                await Task.Delay(150).ConfigureAwait(false);
                if (generation != Volatile.Read(ref changeGeneration) ||
                    !ReferenceEquals(snapshot, buffer.CurrentSnapshot) ||
                    !JavaLspRpc.SameUri(uri, openedUri)) return;
                await EnsureAsync(uri, snapshot, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                WriteLog("edit-error:" + ex.GetType().Name, uri, -1, -1);
            }
        }

        internal void RegisterView(ITextView view)
        {
            if (view == null || view.IsClosed) return;
            lock (registeredViews)
            {
                if (!registeredViews.Add(view)) return;
                Interlocked.Increment(ref openViews);
            }
            view.Closed += OnViewClosed;
        }

        // Start document synchronization on the FIRST editor open, not on the
        // first completion. Give Visual Studio a brief opportunity to send its
        // native didOpen; the latter always takes precedence when observed.
        internal void BeginInitialSync(ITextView view,
            ITextDocumentFactoryService documents)
        {
            var ignored = InitialSyncAsync(view, documents);
        }

        private async Task InitialSyncAsync(ITextView view,
            ITextDocumentFactoryService documents)
        {
            try
            {
                await Task.Delay(400).ConfigureAwait(false);
                for (int attempt = 0; attempt < 120; ++attempt)
                {
                    if (view.IsClosed || !Enabled) return;
                    ITextDocument document;
                    if (documents == null ||
                        !documents.TryGetTextDocument(buffer, out document)) return;
                    if (!JavaCpsWorkspaceContext.IsLoadedJavaDocument(document.FilePath))
                        return;
                    if (JavaLanguageStatus.IsInitialized && JavaLspRpc.Connected)
                    {
                        string uri = JavaLspRpc.FileUri(document);
                        if (await EnsureAsync(uri, buffer.CurrentSnapshot,
                            CancellationToken.None).ConfigureAwait(false)) return;
                    }
                    await Task.Delay(250).ConfigureAwait(false);
                }
                WriteLog("initial-sync-timeout", "file:///unknown.java", -1, -1);
            }
            catch (Exception ex)
            {
                WriteLog("initial-sync-error:" + ex.GetType().Name,
                    "file:///unknown.java", -1, -1);
            }
        }

        private void OnViewClosed(object sender, EventArgs e)
        {
            var view = sender as ITextView;
            if (view == null) return;
            view.Closed -= OnViewClosed;
            lock (registeredViews)
            {
                if (!registeredViews.Remove(view)) return;
                if (Interlocked.Decrement(ref openViews) != 0) return;
            }
            // The last view closed: release only documents opened by this bridge.
            var ignored = CloseAsync();
        }

        internal async Task<bool> EnsureAsync(string uri, ITextSnapshot snapshot,
            CancellationToken token)
        {
            if (!Enabled || snapshot == null ||
                !ReferenceEquals(snapshot, buffer.CurrentSnapshot) ||
                !JavaLanguageStatus.IsInitialized || !JavaLspRpc.Connected)
                return false;
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(snapshot, buffer.CurrentSnapshot) ||
                    !JavaLanguageStatus.IsInitialized || !JavaLspRpc.Connected)
                    return false;
                object identity = JavaLspRpc.ConnectionIdentity;
                if (identity == null) return false;
                // The middleware observes ONLY native Visual Studio document
                // notifications. Our direct JSON-RPC fallback bypasses it.
                // Never create a second didOpen for an already native-owned URI.
                if (JavaDiagnosticsMonitor.IsNativeDocumentOpen(uri))
                {
                    if (openedUri != null && JavaLspRpc.SameUri(uri, openedUri))
                    {
                        // A late native takeover: stop sending fallback changes.
                        // A synthetic didClose here could close the native document.
                        openedUri = null;
                        sessionIdentity = null;
                        lastSnapshotVersion = -1;
                        documentVersion = 0;
                        WriteLog("native-handoff", uri, snapshot.Version.VersionNumber, -1);
                    }
                    return true;
                }
                if (!ReferenceEquals(identity, sessionIdentity) ||
                    !JavaLspRpc.SameUri(uri, openedUri))
                {
                    // The file can be renamed by the project system while open.
                    // Close the old URI before opening the new one, but only if
                    // the old connection still exists.
                    if (openedUri != null && ReferenceEquals(identity, sessionIdentity))
                        await JavaLspRpc.NotifyAsync("textDocument/didClose",
                            new { textDocument = new { uri = openedUri } })
                            .ConfigureAwait(false);
                    openedUri = null;
                    sessionIdentity = null;
                    int initialVersion = 1;
                    bool sent = await JavaLspRpc.NotifyAsync("textDocument/didOpen",
                        new { textDocument = new {
                            uri, languageId = "java", version = initialVersion,
                            text = snapshot.GetText() } }).ConfigureAwait(false);
                    if (!sent) return false;
                    openedUri = uri;
                    sessionIdentity = identity;
                    documentVersion = initialVersion;
                    lastSnapshotVersion = snapshot.Version.VersionNumber;
                    WriteLog("didOpen", uri, lastSnapshotVersion, documentVersion);
                }
                else if (snapshot.Version.VersionNumber != lastSnapshotVersion)
                {
                    int nextVersion = documentVersion + 1;
                    bool sent = await JavaLspRpc.NotifyAsync("textDocument/didChange",
                        new { textDocument = new { uri, version = nextVersion },
                            contentChanges = new[] { new { text = snapshot.GetText() } } })
                        .ConfigureAwait(false);
                    if (!sent) return false;
                    documentVersion = nextVersion;
                    lastSnapshotVersion = snapshot.Version.VersionNumber;
                    WriteLog("didChange", uri, lastSnapshotVersion, documentVersion);
                }
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                WriteLog("sync-error:" + ex.GetType().Name, uri, -1, -1);
                return false;
            }
            finally { gate.Release(); }
        }

        private async Task CloseAsync()
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref openViews) != 0) return;
                string previous = openedUri;
                if (previous != null && ReferenceEquals(
                    sessionIdentity, JavaLspRpc.ConnectionIdentity))
                {
                    try
                    {
                        await JavaLspRpc.NotifyAsync("textDocument/didClose",
                            new { textDocument = new { uri = previous } })
                            .ConfigureAwait(false);
                        WriteLog("didClose", previous, lastSnapshotVersion, documentVersion);
                    }
                    catch (Exception ex)
                    {
                        WriteLog("close-error:" + ex.GetType().Name,
                            previous, -1, -1);
                    }
                }
                openedUri = null;
                sessionIdentity = null;
                documentVersion = 0;
                lastSnapshotVersion = -1;
            }
            finally { gate.Release(); }
        }

        private static void WriteLog(string action, string uri,
            int snapshotVersion, int lspVersion)
        {
            // Only operation, filename and versions; never log Java source text.
            try
            {
                var folder = Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                    "JavaLanguageSupportVS", "logs");
                Directory.CreateDirectory(folder);
                string filename = new Uri(uri).LocalPath;
                File.AppendAllText(Path.Combine(folder, "cps-completion-sync.log"),
                    DateTime.Now.ToString("O") + " " + action + " " +
                    Path.GetFileName(filename) + " snapshot=" + snapshotVersion +
                    " lsp=" + lspVersion + Environment.NewLine);
            }
            catch (Exception) { /* diagnostic logging is never fatal */ }
        }
    }
}
