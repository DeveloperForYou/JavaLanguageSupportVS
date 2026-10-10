using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text.RegularExpressions;
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
        }

        public event EventHandler<EventArgs> SuggestedActionsChanged;

        // La Light Bulb chiama GetSuggestedActions in modo sincrono, specialmente
        // con Ctrl+.; non possiamo restituire una lista vuota mentre aspettiamo
        // la risposta del language server. Esponiamo una voce padre immediata,
        // poi recuperiamo le alternative quando l'utente apre il sottomenu.
        public Task<bool> HasSuggestedActionsAsync(ISuggestedActionCategorySet categories,
            SnapshotSpan range, CancellationToken cancellationToken)
        {
            string name;
            return Task.FromResult(TryGetImportName(out name));
        }

        public IEnumerable<SuggestedActionSet> GetSuggestedActions(ISuggestedActionCategorySet categories,
            SnapshotSpan range, CancellationToken cancellationToken)
        {
            string name;
            if (!TryGetImportName(out name))
                return Enumerable.Empty<SuggestedActionSet>();

            ISuggestedAction parent = new JavaImportChoicesAction(
                this, range, name, buffer.CurrentSnapshot.Version.VersionNumber);
            return new[] { new SuggestedActionSet(new[] { parent }) };
        }

        private bool TryGetImportName(out string name)
        {
            name = null;
            if (disposed || !JavaLspRpc.Connected || buffer == null || view == null)
                return false;
            var snapshot = buffer.CurrentSnapshot;
            int caret = Math.Min(view.Caret.Position.BufferPosition.Position, snapshot.Length);
            int end;
            if (!TryGetUnqualifiedIdentifier(snapshot, caret, out name, out end))
                return false;
            // Le Java types possono essere annotate/enumerate; il criterio
            // convenzionale evita di mostrare l'azione su ogni variabile.
            if (name.Length == 0 || !char.IsUpper(name[0]) ||
                HasExplicitImport(snapshot.GetText(), name))
                return false;
            return true;
        }

        internal async Task<IEnumerable<SuggestedActionSet>> GetImportChoiceSetsAsync(
            SnapshotSpan range, string expectedName, int expectedVersion,
            CancellationToken cancellationToken)
        {
            if (disposed || buffer.CurrentSnapshot.Version.VersionNumber != expectedVersion)
                return Enumerable.Empty<SuggestedActionSet>();
            string actualName;
            if (!TryGetImportName(out actualName) || actualName != expectedName)
                return Enumerable.Empty<SuggestedActionSet>();

            List<JObject> matches;
            try
            {
                matches = await GetActionsAsync(range, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Enumerable.Empty<SuggestedActionSet>();
            }
            if (matches == null) matches = new List<JObject>();
            JavaLanguageStatus.LspResponse("java/importChoices", matches.Count, false, false);

            var actions = matches.Where(a => a["edit"] is JObject)
                .GroupBy(a => (string)a["title"] ?? "", StringComparer.Ordinal)
                .Select(g => g.First())
                .Select(a => (ISuggestedAction)new JavaImportSuggestedAction(
                    buffer, documents, (string)a["title"], a["edit"] as JObject,
                    expectedVersion)).ToArray();
            if (actions.Length == 0)
            {
                // Non inventiamo pacchetti: se JDT LS non rende visibili tipi
                // alternativi nel classpath, l'utente riceve un esito esplicito.
                actions = new ISuggestedAction[] { new JavaNoImportOptionsAction(expectedName) };
            }
            return new[] { new SuggestedActionSet(actions) };
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
            JavaLanguageStatus.LspRequest("java/importChoices");
            JToken response;
            try { response = await JavaLspRpc.RequestAsync("textDocument/codeAction", parameters, token)
                    .ConfigureAwait(false); }
            catch (OperationCanceledException) { return empty; }
            // Alcune Code Actions possono mancare quando JDT LS non ha ancora
            // prodotto una diagnostica per il simbolo ambiguo.
            var array = response as JArray ?? new JArray();

            // Import con modifica già calcolata. Le azioni command-only vengono tracciate
            // dalla diagnostica ma non eseguite automaticamente (evita effetti collaterali).
            var candidates = array.OfType<JObject>()
                .Where(a => ((string)a["title"] ?? "").IndexOf("import", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(NormalizeImport)
                .Where(a => a != null)
                .Take(20).ToList();
            // Cerca anche le classi con lo stesso nome nel classpath JDT LS.
            // Ogni FQN diventa un'azione separata: la scelta resta all'utente.
            await AddImportChoicesAsync(candidates, snapshot, offset, uri, token)
                .ConfigureAwait(false);
            lock (sync)
            {
                if (!disposed && snapshot.Version.VersionNumber == buffer.CurrentSnapshot.Version.VersionNumber)
                {
                    cacheKey = key;
                    cachedActions = candidates;
                }
            }
            return candidates;
        }


        // Ricerca GENERICA: recupera i FQN dalle Code Actions e, se necessari,
        // dai risultati indicizzati e dai completamenti del language server.
        // L'utente sceglie ogni alternativa in Ctrl+.: mai un import automatico.
        private static async Task AddImportChoicesAsync(List<JObject> actions,
            ITextSnapshot snapshot, int offset, string uri, CancellationToken token)
        {
            string simpleName;
            int identifierEnd;
            if (!TryGetUnqualifiedIdentifier(snapshot, offset, out simpleName,
                out identifierEnd)) return;
            string source = snapshot.GetText();
            if (HasExplicitImport(source, simpleName)) return;

            // workspace/symbol trova i sorgenti e, quando abilitati dal client,
            // anche i JAR/JDK. completion aggiunge alternative di libreria che
            // JDT LS potrebbe omettere da workspace/symbol.
            Task<JToken> symbolsTask = JavaLspRpc.RequestAsync("workspace/symbol",
                new { query = simpleName }, token);
            Task<JToken> completionTask = JavaLspRpc.RequestAsync("textDocument/completion",
                new
                {
                    textDocument = new { uri },
                    position = JavaLspRpc.Position(snapshot, identifierEnd),
                    context = new { triggerKind = 1 }
                }, token);

            JToken symbolsResponse, completionsResponse;
            try
            {
                await Task.WhenAll(symbolsTask, completionTask).ConfigureAwait(false);
                symbolsResponse = symbolsTask.Result;
                completionsResponse = completionTask.Result;
            }
            catch (OperationCanceledException) { return; }

            var found = new HashSet<string>(StringComparer.Ordinal);
            var symbols = symbolsResponse as JArray;
            if (symbols != null)
            {
                foreach (var symbol in symbols.OfType<JObject>())
                {
                    // JDT LS usa anche Property=7 per i tipi annotation.
                    // Class=5, Property(annotation)=7, Enum=10, Interface=11, Struct=23.
                    int kind = (int?)symbol["kind"] ?? -1;
                    if (kind != 5 && kind != 7 && kind != 10 && kind != 11 && kind != 23) continue;
                    if (!string.Equals((string)symbol["name"], simpleName,
                        StringComparison.Ordinal)) continue;
                    string container = (string)symbol["containerName"];
                    string qualified = container + "." + simpleName;
                    if (IsJavaQualifiedName(qualified)) found.Add(qualified);
                }
            }

            // LSP CompletionItemKind: Class=7, Interface=8, Enum=13,
            // Annotation=15, Struct=22.
            // Leggiamo solo informazioni esplicite sulla provenienza della classe,
            // non indoviniamo package da nomi noti o regole euristiche.
            var completions = completionsResponse as JArray ??
                (completionsResponse as JObject)?["items"] as JArray;
            if (completions != null)
            {
                foreach (var item in completions.OfType<JObject>())
                {
                    int kind = (int?)item["kind"] ?? -1;
                    if (kind != 7 && kind != 8 && kind != 13 && kind != 15 && kind != 22) continue;
                    if (!string.Equals((string)item["label"], simpleName,
                        StringComparison.Ordinal)) continue;
                    AddCompletionQualifiedNames(found, item, simpleName);
                }
            }

            foreach (string qualifiedName in found.OrderBy(s => s, StringComparer.Ordinal))
            {
                if (token.IsCancellationRequested) return;
                if (IsAlreadyImported(source, qualifiedName)) continue;
                string container = qualifiedName.Substring(0,
                    qualifiedName.Length - simpleName.Length - 1);

                // Le azioni LSP prevalgono: possono contenere un WorkspaceEdit
                // piu' ricco della semplice aggiunta di un import.
                if (actions.Any(a => IsEquivalentImport(a, qualifiedName, simpleName, container)))
                    continue;
                actions.Add(new JObject
                {
                    ["title"] = "Import '" + simpleName + "' (" + container + ")",
                    ["edit"] = CreateImportEdit(snapshot, uri, qualifiedName)
                });
            }
        }

        private static void AddCompletionQualifiedNames(HashSet<string> names,
            JObject item, string simpleName)
        {
            // JDT LS puo' indicare un FQN in detail/filterText oppure il package
            // in labelDetails.description, secondo la versione e il contesto.
            foreach (string value in new[] {
                (string)item["detail"], (string)item["filterText"],
                (string)item.SelectToken("labelDetails.description") })
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                string trimmed = value.Trim();
                if (trimmed.StartsWith("(") && trimmed.EndsWith(")"))
                    trimmed = trimmed.Substring(1, trimmed.Length - 2).Trim();
                // Non trattare dettagli descrittivi, il filterText "List" o
                // un tipo di ritorno (es. java.lang.String) come un package.
                if (trimmed.IndexOf('.') < 0) continue;
                string qualified;
                if (trimmed.EndsWith("." + simpleName, StringComparison.Ordinal))
                    qualified = trimmed;
                else
                {
                    string finalSegment = trimmed.Substring(trimmed.LastIndexOf('.') + 1);
                    if (finalSegment.Length == 0 || !char.IsLower(finalSegment[0]))
                        continue;
                    qualified = trimmed + "." + simpleName;
                }
                if (IsJavaQualifiedName(qualified)) names.Add(qualified);
            }
            var additional = item["additionalTextEdits"] as JArray;
            if (additional == null) return;
            foreach (var edit in additional.OfType<JObject>())
            {
                string newText = (string)edit["newText"] ?? "";
                foreach (Match match in Regex.Matches(newText,
                    @"\bimport\s+([\w.$]+)\s*;"))
                {
                    string qualified = match.Groups[1].Value;
                    if (qualified.EndsWith("." + simpleName, StringComparison.Ordinal) &&
                        IsJavaQualifiedName(qualified)) names.Add(qualified);
                }
            }
        }

        private static bool TryGetUnqualifiedIdentifier(ITextSnapshot snapshot,
            int offset, out string name, out int identifierEnd)
        {
            name = null;
            identifierEnd = offset;
            if (snapshot.Length == 0 || offset < 0 || offset > snapshot.Length) return false;
            string text = snapshot.GetText();
            int start = offset, end = offset;
            while (start > 0 && IsIdentifierPart(text[start - 1])) start--;
            while (end < text.Length && IsIdentifierPart(text[end])) end++;
            if (start == end || !IsIdentifierStart(text[start])) return false;
            // Una parola gia' qualificata (java.util.List) non richiede import.
            if (start > 0 && text[start - 1] == '.') return false;
            if (end < text.Length && text[end] == '.') return false;

            var line = snapshot.GetLineFromPosition(start);
            string prefix = text.Substring(line.Start.Position,
                start - line.Start.Position);
            var trimmed = prefix.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
                trimmed.StartsWith("*", StringComparison.Ordinal) ||
                trimmed.StartsWith("import ", StringComparison.Ordinal) ||
                trimmed.StartsWith("package ", StringComparison.Ordinal) ||
                prefix.Contains("//")) return false;

            name = text.Substring(start, end - start);
            identifierEnd = end;
            return true;
        }

        private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_' || c == '$';
        private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

        private static bool IsJavaQualifiedName(string name)
        {
            string[] parts = name.Split('.');
            if (parts.Length < 2) return false;
            return parts.All(p => p.Length != 0 && IsIdentifierStart(p[0]) &&
                p.Skip(1).All(IsIdentifierPart));
        }

        private static bool HasExplicitImport(string source, string simpleName)
        {
            // Non aggiungiamo un secondo import con lo stesso nome semplice:
            // produrrebbe un errore Java, e l'utente deve risolvere quello esistente.
            var matches = Regex.Matches(source,
                @"(?m)^[ \t]*import[ \t]+(?!static\b)([\w.$]+)[ \t]*;");
            return matches.Cast<Match>().Any(m => m.Groups[1].Value.EndsWith(
                "." + simpleName, StringComparison.Ordinal));
        }

        private static bool IsAlreadyImported(string source, string qualifiedName)
        {
            return Regex.IsMatch(source,
                @"(?m)^[ \t]*import[ \t]+" + Regex.Escape(qualifiedName) + @"[ \t]*;");
        }

        private static bool IsEquivalentImport(JObject action, string qualifiedName,
            string simpleName, string container)
        {
            string title = (string)action["title"] ?? "";
            if (title.IndexOf(qualifiedName, StringComparison.Ordinal) >= 0 ||
                (title.IndexOf(simpleName, StringComparison.Ordinal) >= 0 &&
                 title.IndexOf("(" + container + ")", StringComparison.Ordinal) >= 0))
                return true;
            var edit = action["edit"] as JObject;
            if (edit == null) return false;
            return edit.ToString(Newtonsoft.Json.Formatting.None).IndexOf(
                "import " + qualifiedName + ";", StringComparison.Ordinal) >= 0;
        }

        private static JObject CreateImportEdit(ITextSnapshot snapshot, string uri, string type)
        {
            string source = snapshot.GetText();
            string eol = source.Contains("\r\n") ? "\r\n" : "\n";
            int insertAt = 0;
            string newText;
            // Aggiungi dopo l'ultimo import; altrimenti dopo package o all'inizio.
            var imports = Regex.Matches(source,
                @"(?m)^[ \t]*import[ \t]+(?:static[ \t]+)?[\w.*]+[ \t]*;[ \t]*(?:\r?\n|$)");
            if (imports.Count != 0)
            {
                var last = imports[imports.Count - 1];
                insertAt = last.Index + last.Length;
                newText = "import " + type + ";" + eol;
            }
            else
            {
                var package = Regex.Match(source,
                    @"(?m)^[ \t]*package[ \t]+[\w.]+[ \t]*;[ \t]*(?:\r?\n|$)");
                if (package.Success)
                {
                    insertAt = package.Index + package.Length;
                    newText = "import " + type + ";" + eol;
                }
                else newText = "import " + type + ";" + eol + eol;
            }
            var pos = JavaLspRpc.Position(snapshot, insertAt);
            var edit = new JObject
            {
                ["changes"] = new JObject
                {
                    [uri] = new JArray(new JObject
                    {
                        ["range"] = new JObject
                        {
                            ["start"] = JToken.FromObject(pos),
                            ["end"] = JToken.FromObject(pos)
                        },
                        ["newText"] = newText
                    })
                }
            };
            return edit;
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

    // Sottomenu asincrono: l'opzione principale e' visibile immediatamente,
    // e le singole alternative vengono create al momento dell'espansione.
    internal sealed class JavaImportChoicesAction : ISuggestedAction
    {
        private readonly JavaImportSuggestedActionsSource source;
        private readonly SnapshotSpan range;
        private readonly string name;
        private readonly int version;

        internal JavaImportChoicesAction(JavaImportSuggestedActionsSource source,
            SnapshotSpan range, string name, int version)
        {
            this.source = source;
            this.range = range;
            this.name = name;
            this.version = version;
        }

        public string DisplayText => "Scegli import per '" + name + "'...";
        public bool HasActionSets => true;
        public bool HasPreview => false;
        public string IconAutomationText => null;
        public string InputGestureText => null;
        public ImageMoniker IconMoniker => default(ImageMoniker);
        public Task<object> GetPreviewAsync(CancellationToken token) => Task.FromResult<object>(null);
        public Task<IEnumerable<SuggestedActionSet>> GetActionSetsAsync(CancellationToken token)
            => source.GetImportChoiceSetsAsync(range, name, version, token);
        public void Invoke(CancellationToken token) { /* Voce di sottomenu: nessuna modifica */ }
        public bool TryGetTelemetryId(out Guid telemetryId) { telemetryId = Guid.Empty; return false; }
        public void Dispose() { }
    }

    internal sealed class JavaNoImportOptionsAction : ISuggestedAction
    {
        private readonly string name;
        internal JavaNoImportOptionsAction(string name) { this.name = name; }
        public string DisplayText => "Nessuna alternativa trovata per '" + name + "' nel classpath JDT LS";
        public bool HasActionSets => false;
        public bool HasPreview => false;
        public string IconAutomationText => null;
        public string InputGestureText => null;
        public ImageMoniker IconMoniker => default(ImageMoniker);
        public Task<object> GetPreviewAsync(CancellationToken token) => Task.FromResult<object>(null);
        public Task<IEnumerable<SuggestedActionSet>> GetActionSetsAsync(CancellationToken token)
            => Task.FromResult<IEnumerable<SuggestedActionSet>>(null);
        public void Invoke(CancellationToken token) { }
        public bool TryGetTelemetryId(out Guid telemetryId) { telemetryId = Guid.Empty; return false; }
        public void Dispose() { }
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
