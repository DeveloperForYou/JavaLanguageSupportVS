using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json.Linq;

namespace JavaLanguageSupport
{
    // Light bulb nativa per import proposti da JDT LS.
    // Solo WorkspaceEdit locali e direttamente applicabili: niente comandi non verificati.
    [Export(typeof(ISuggestedActionsSourceProvider))]
    [Name("Java JDT LS Imports")]
    [ContentType("java")]
    internal sealed class JavaImportSuggestedActionsProvider : ISuggestedActionsSourceProvider
    {
        [Import] internal ITextDocumentFactoryService Documents = null;

        public ISuggestedActionsSource CreateSuggestedActionsSource(ITextView textView, ITextBuffer textBuffer)
            => new JavaImportSuggestedActionsSource(textView, textBuffer, Documents);
    }

    internal sealed class JavaImportSuggestedActionsSource : ISuggestedActionsSource
    {
        private readonly ITextView view;
        private readonly ITextBuffer buffer;
        private readonly ITextDocumentFactoryService documents;
        private readonly SynchronizationContext uiContext;
        private readonly object sync = new object();
        private string cacheKey;
        private List<JObject> cachedActions;
        private bool disposed;

        internal JavaImportSuggestedActionsSource(ITextView view, ITextBuffer buffer,
            ITextDocumentFactoryService documents)
        {
            this.view = view;
            this.buffer = buffer;
            this.documents = documents;
            uiContext = SynchronizationContext.Current;
        }

        public event EventHandler<EventArgs> SuggestedActionsChanged;

        public async Task<bool> HasSuggestedActionsAsync(ISuggestedActionCategorySet categories,
            SnapshotSpan range, CancellationToken cancellationToken)
        {
            var actions = await GetActionsAsync(range, cancellationToken).ConfigureAwait(false);
            return actions.Count != 0;
        }

        public IEnumerable<SuggestedActionSet> GetSuggestedActions(ISuggestedActionCategorySet categories,
            SnapshotSpan range, CancellationToken cancellationToken)
        {
            // Questa API e' sincrona. Non blocchiamo il thread dell'IDE in attesa di JDT LS:
            // l'evento SuggestedActionsChanged aggiorna la lampadina quando arriva la risposta.
            string key = Key(range);
            List<JObject> values;
            lock (sync) values = cacheKey == key ? cachedActions : null;
            if (values == null)
            {
                _ = GetActionsAsync(range, cancellationToken);
                return Enumerable.Empty<SuggestedActionSet>();
            }
            var actions = values.Select(a => (ISuggestedAction)new JavaImportSuggestedAction(
                buffer, documents, (string)a["title"], a["edit"] as JObject,
                buffer.CurrentSnapshot.Version.VersionNumber)).ToArray();
            return actions.Length == 0 ? Enumerable.Empty<SuggestedActionSet>()
                : new[] { new SuggestedActionSet(actions) };
        }

        private string Key(SnapshotSpan range)
        {
            var snapshot = buffer.CurrentSnapshot;
            var point = view.Caret.Position.BufferPosition;
            int offset = Math.Min(point.Position, snapshot.Length);
            return snapshot.Version.VersionNumber + ":" + offset;
        }

        private async Task<List<JObject>> GetActionsAsync(SnapshotSpan range, CancellationToken token)
        {
            var empty = new List<JObject>();
            if (disposed || !JavaLspRpc.Connected) return empty;
            ITextDocument document;
            if (!documents.TryGetTextDocument(buffer, out document)) return empty;
            string uri = JavaLspRpc.FileUri(document);
            if (uri == null) return empty;
            var snapshot = buffer.CurrentSnapshot;
            var caret = view.Caret.Position.BufferPosition;
            int offset = Math.Min(caret.Position, snapshot.Length);
            string key = Key(range);
            lock (sync)
                if (cacheKey == key && cachedActions != null) return cachedActions;

            var position = JavaLspRpc.Position(snapshot, offset);
            var parameters = new
            {
                textDocument = new { uri },
                range = new { start = position, end = position },
                context = new { diagnostics = new object[0], triggerKind = 1 }
            };
            JToken response;
            try { response = await JavaLspRpc.RequestAsync("textDocument/codeAction", parameters, token)
                    .ConfigureAwait(false); }
            catch (OperationCanceledException) { return empty; }
            var array = response as JArray;
            if (array == null) return empty;

            // Import con modifica già calcolata. Le azioni command-only vengono tracciate
            // dalla diagnostica ma non eseguite automaticamente (evita effetti collaterali).
            var candidates = array.OfType<JObject>()
                .Where(a => ((string)a["title"] ?? "").IndexOf("import", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(NormalizeImport)
                .Where(a => a != null)
                .Take(20).ToList();
            lock (sync)
            {
                if (!disposed && snapshot.Version.VersionNumber == buffer.CurrentSnapshot.Version.VersionNumber)
                {
                    cacheKey = key;
                    cachedActions = candidates;
                }
            }
            if (uiContext != null)
                uiContext.Post(_ => { if (!disposed) SuggestedActionsChanged?.Invoke(this, EventArgs.Empty); }, null);
            return candidates;
        }


        private static JObject NormalizeImport(JObject entry)
        {
            JObject edit = entry["edit"] as JObject;
            if (edit == null)
            {
                // JDT LS può restituire direttamente un Command LSP invece di una
                // CodeAction. Il comando java.apply.workspaceEdit contiene già
                // il WorkspaceEdit: non bisogna inviarlo al server come executeCommand.
                JToken command = entry["command"];
                string name = command is JObject ? (string)command["command"] : (string)command;
                if (name != "java.apply.workspaceEdit") return null;
                var args = (command is JObject ? command["arguments"] : entry["arguments"]) as JArray;
                if (args == null || args.Count == 0) return null;
                edit = args[0] as JObject;
            }
            if (edit == null) return null;
            return new JObject { ["title"] = (string)entry["title"], ["edit"] = edit.DeepClone() };
        }

        public bool TryGetTelemetryId(out Guid telemetryId) { telemetryId = Guid.Empty; return false; }
        public void Dispose() { lock (sync) { disposed = true; cachedActions = null; } }
    }

    internal sealed class JavaImportSuggestedAction : ISuggestedAction
    {
        private readonly ITextBuffer buffer;
        private readonly JObject edit;
        private readonly ITextDocumentFactoryService documents;
        private readonly int expectedVersion;
        public string DisplayText { get; }
        internal JavaImportSuggestedAction(ITextBuffer buffer, ITextDocumentFactoryService documents,
            string title, JObject edit, int expectedVersion)
        {
            this.buffer = buffer;
            this.documents = documents;
            this.edit = edit;
            this.expectedVersion = expectedVersion;
            DisplayText = title ?? "Import Java";
        }

        public bool HasActionSets => false;
        public bool HasPreview => false;
        public string IconAutomationText => null;
        public string InputGestureText => null;
        public ImageMoniker IconMoniker => default(ImageMoniker);
        public Task<object> GetPreviewAsync(CancellationToken token) => Task.FromResult<object>(null);
        public Task<IEnumerable<SuggestedActionSet>> GetActionSetsAsync(CancellationToken token)
            => Task.FromResult<IEnumerable<SuggestedActionSet>>(null);

        public void Invoke(CancellationToken token)
        {
            try
            {
                // Per sicurezza si applicano soltanto modifiche a QUESTO documento aperto.
                // Non si scrivono file sconosciuti su disco.
                ITextDocument document;
                if (!documents.TryGetTextDocument(buffer, out document))
                    throw new InvalidOperationException("Documento Java non disponibile.");
                string uri = JavaLspRpc.FileUri(document);
                var changes = new List<JObject>();
                var changeMap = edit["changes"] as JObject;
                if (changeMap != null)
                {
                    foreach (var property in changeMap.Properties())
                    {
                        if (!JavaLspRpc.SameUri(property.Name, uri))
                            throw new NotSupportedException("WorkspaceEdit multi-file: non applicato.");
                        var values = property.Value as JArray;
                        if (values == null) throw new InvalidOperationException("WorkspaceEdit non valido.");
                        changes.AddRange(values.OfType<JObject>());
                    }
                }
                var documentChanges = edit["documentChanges"] as JArray;
                if (documentChanges != null)
                {
                    foreach (var change in documentChanges.OfType<JObject>())
                    {
                        if (!JavaLspRpc.SameUri((string)change.SelectToken("textDocument.uri"), uri))
                            throw new NotSupportedException("WorkspaceEdit multi-file: non applicato.");
                        var values = change["edits"] as JArray;
                        if (values == null) throw new InvalidOperationException("WorkspaceEdit non valido.");
                        changes.AddRange(values.OfType<JObject>());
                    }
                }
                if (changes.Count == 0) return;
                var snapshot = buffer.CurrentSnapshot;
                if (snapshot.Version.VersionNumber != expectedVersion)
                    throw new InvalidOperationException("Il documento è cambiato: richiedere nuovamente l'import.");
                var replacements = changes.Select(c =>
                {
                    var r = c["range"];
                    int start = Offset(snapshot, r?["start"]);
                    int end = Offset(snapshot, r?["end"]);
                    if (end < start) throw new InvalidOperationException("Range LSP invertito.");
                    return new { start, end, text = (string)c["newText"] ?? "" };
                }).OrderByDescending(e => e.start).ToList();
                // Più inserimenti a lunghezza zero sullo stesso offset sono leciti in LSP.
                // Si concatenano nello stesso ordine ricevuto, prima di creare l'edit VS.
                var merged = new List<Tuple<int, int, string>>();
                foreach (var r in replacements)
                {
                    if (merged.Count > 0 && r.start == r.end &&
                        merged[merged.Count - 1].Item1 == r.start &&
                        merged[merged.Count - 1].Item2 == r.end)
                    {
                        var last = merged[merged.Count - 1];
                        // Ordina per start stabile: il primo testo LSP resta davanti.
                        merged[merged.Count - 1] = Tuple.Create(r.start, r.end, last.Item3 + r.text);
                    }
                    else merged.Add(Tuple.Create(r.start, r.end, r.text));
                }
                int previousStart = snapshot.Length + 1;
                foreach (var r in merged)
                {
                    if (r.Item2 > previousStart)
                        throw new InvalidOperationException("Modifiche LSP sovrapposte.");
                    previousStart = r.Item1;
                }
                using (var textEdit = buffer.CreateEdit())
                {
                    foreach (var r in merged) textEdit.Replace(r.Item1, r.Item2 - r.Item1, r.Item3);
                    textEdit.Apply();
                }
            }
            catch (Exception ex)
            {
                JavaLanguageStatus.LspFailure("Java import/applyEdit", ex.Message);
            }
        }

        private static int Offset(ITextSnapshot snapshot, JToken point)
        {
            if (point == null) throw new InvalidOperationException("Range LSP assente.");
            int line = (int)point["line"], column = (int)point["character"];
            if (line < 0 || line >= snapshot.LineCount)
                throw new ArgumentOutOfRangeException("line");
            var textLine = snapshot.GetLineFromLineNumber(line);
            if (column < 0 || column > textLine.Length)
                throw new ArgumentOutOfRangeException("character");
            return textLine.Start.Position + column;
        }

        public bool TryGetTelemetryId(out Guid telemetryId) { telemetryId = Guid.Empty; return false; }
        public void Dispose() { }
    }
}
