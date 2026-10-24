using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Newtonsoft.Json.Linq;
using Task = System.Threading.Tasks.Task;

// PACK C3: refactoring robustness and signature validation.
namespace JavaLanguageSupport.Editor.Refactoring
{
    /// <summary>
    /// Explicit selection-based Java refactorings. JDT LS is the ONLY source of
    /// transformations; no regex-based source rewriting or second language server.
    /// The actual WorkspaceEdit is applied by the Pack 4 service.
    /// </summary>
    internal static class JavaExtractRefactorings
    {
        internal static bool Supports(JavaEditorOperation operation)
        {
            return operation == JavaEditorOperation.ExtractMethod ||
                operation == JavaEditorOperation.ExtractVariable ||
                operation == JavaEditorOperation.ExtractConstant ||
                operation == JavaEditorOperation.InlineVariable;
        }

        internal static async Task ExecuteAsync(JavaEditorOperation operation, string uri,
            ITextBuffer buffer, ITextSnapshot snapshot, SnapshotSpan selection, int caret,
            Action<string> log, CancellationToken token)
        {
            if (!Supports(operation)) return;
            if (buffer == null || snapshot == null ||
                !ReferenceEquals(snapshot, buffer.CurrentSnapshot))
            {
                Log(log, "Il file Java e' cambiato. Ripeti il refactoring.");
                return;
            }
            bool needsSelection = operation != JavaEditorOperation.InlineVariable;
            if (needsSelection && selection.IsEmpty)
            {
                await NotifyAsync("Seleziona prima l'espressione o le istruzioni Java da estrarre.", token);
                return;
            }
            if (selection.Snapshot != snapshot || selection.Snapshot.TextBuffer != buffer)
            {
                Log(log, "Selezione non valida per il documento corrente.");
                return;
            }
            if (!selection.IsEmpty && selection.Length > 64000)
            {
                await NotifyAsync("Selezione troppo grande: riduci il blocco Java da estrarre.", token);
                return;
            }
            token.ThrowIfCancellationRequested();

            int start = selection.IsEmpty ? caret : selection.Start.Position;
            int end = selection.IsEmpty ? caret : selection.End.Position;
            if (operation == JavaEditorOperation.InlineVariable && selection.IsEmpty)
            {
                // JDT LS needs the local-variable IDENTIFIER range; a zero-width
                // caret range often produces no 'Inline local variable' action.
                if (!TryIdentifierRange(snapshot, caret, out start, out end))
                {
                    await NotifyAsync("Posiziona il cursore sul nome di una variabile locale Java.", token);
                    return;
                }
            }
            var row = snapshot.GetLineFromPosition(Math.Min(caret, snapshot.Length));
            JArray diagnostics = JavaDiagnosticsMonitor.ForPosition(uri, row.LineNumber,
                Math.Min(caret, snapshot.Length) - row.Start.Position);

            // Do NOT send context.only: JDT LS versions differ in which kinds they
            // advertise (some return refactor or a Command without kind).
            var request = new
            {
                textDocument = new { uri },
                range = new {
                    start = JavaLspRpc.Position(snapshot, start),
                    end = JavaLspRpc.Position(snapshot, end)
                },
                context = new { diagnostics }
            };
            JToken response = await JavaLspRpc.RequestAsync("textDocument/codeAction",
                request, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var array = response as JArray;
            var choices = FilterUsableActions(array, operation);

            // Selection edges can contain only indentation or a trailing newline.
            // Some JDT LS builds reject that range: retry ONCE with whitespace
            // trimmed, without changing the selection displayed in Visual Studio.
            // Never retry a request whose source snapshot has already changed.
            if (choices.Count == 0 && needsSelection && end > start &&
                ReferenceEquals(buffer.CurrentSnapshot, snapshot))
            {
                int cleanStart = start, cleanEnd = end;
                while (cleanStart < cleanEnd && char.IsWhiteSpace(snapshot[cleanStart]))
                    cleanStart++;
                while (cleanEnd > cleanStart && char.IsWhiteSpace(snapshot[cleanEnd - 1]))
                    cleanEnd--;
                if (cleanEnd > cleanStart && (cleanStart != start || cleanEnd != end))
                {
                    var retryRange = new
                    {
                        textDocument = new { uri },
                        range = new {
                            start = JavaLspRpc.Position(snapshot, cleanStart),
                            end = JavaLspRpc.Position(snapshot, cleanEnd)
                        },
                        context = new { diagnostics }
                    };
                    JToken trimmed = await JavaLspRpc.RequestAsync(
                        "textDocument/codeAction", retryRange, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(buffer.CurrentSnapshot, snapshot))
                    {
                        Log(log, "Documento modificato durante il refactoring: riprova.");
                        return;
                    }
                    var trimmedArray = trimmed as JArray;
                    var trimmedChoices = FilterUsableActions(trimmedArray, operation);
                    if (trimmedChoices.Count > 0)
                    {
                        choices = trimmedChoices;
                        array = trimmedArray;
                        Log(log, "Java C3: selezione normalizzata per JDT LS.");
                    }
                }
            }
            // Older JDT LS builds may offer inline refactoring at a caret,
            // not a range. Retry with zero-width caret ONLY for explicit Inline.
            if (choices.Count == 0 && operation == JavaEditorOperation.InlineVariable &&
                end > start && ReferenceEquals(buffer.CurrentSnapshot, snapshot))
            {
                var retryRequest = new
                {
                    textDocument = new { uri },
                    range = new { start = JavaLspRpc.Position(snapshot, caret),
                                  end = JavaLspRpc.Position(snapshot, caret) },
                    context = new { diagnostics }
                };
                JToken retry = await JavaLspRpc.RequestAsync("textDocument/codeAction",
                    retryRequest, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var extra = retry as JArray;
                if (extra != null)
                {
                    // Include the results in troubleshooting without guessing
                    // at edits or blindly running unrelated refactorings.
                    choices.AddRange(FilterUsableActions(extra, operation));
                    // Keep one entry per distinct LSP action; same-title
                    // alternatives with different edits remain selectable.
                    choices = DistinctActions(choices);
                    if (array == null) array = extra;
                }
            }
            if (choices.Count == 0)
            {
                if (array != null && array.OfType<JObject>().Any(a =>
                    IsMatching(a, operation) && a["disabled"] != null &&
                    a["disabled"].Type != JTokenType.Null))
                    Log(log, "JDT LS ha restituito solo refactoring disabilitati per questa posizione.");
                if (array != null && log != null)
                    log("JDT LS Code Actions disponibili: " + string.Join(" | ",
                        array.OfType<JObject>().Take(25).Select(x =>
                            ((string)x["title"] ?? "(senza titolo)") +
                            " [" + ((string)x["kind"] ?? "senza kind") + "]")));
                await NotifyAsync("JDT LS non propone '" + Label(operation) +
                    "' per questa selezione. Verifica che il file sia in un progetto " +
                    "Java indicizzato e che la selezione sia valida.", token);
                return;
            }
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            if (!ReferenceEquals(buffer.CurrentSnapshot, snapshot))
            {
                Log(log, "Il codice Java e' cambiato durante la richiesta. Riprova.");
                return;
            }
            JObject chosen = choices.Count == 1 ? choices[0] : JavaEditorDialogs.Choose(choices);
            if (chosen == null) return;
            await JavaEditorLspOperations.ApplySuggestedCodeActionAsync(chosen,
                buffer, snapshot, uri, log, token);
        }

        // An action can be disabled by JDT LS even if it has a matching title.
        // Never synthesize a WorkspaceEdit or execute such commands.
        private static List<JObject> FilterUsableActions(JArray actions,
            JavaEditorOperation operation)
        {
            if (actions == null) return new List<JObject>();
            return DistinctActions(actions.OfType<JObject>()
                .Where(a => (a["disabled"] == null ||
                             a["disabled"].Type == JTokenType.Null) &&
                            IsMatching(a, operation)));
        }

        private static List<JObject> DistinctActions(IEnumerable<JObject> actions)
        {
            return actions.GroupBy(a => a.ToString(Newtonsoft.Json.Formatting.None),
                    StringComparer.Ordinal)
                .Select(g => g.First()).Take(80).ToList();
        }

        private static bool TryIdentifierRange(ITextSnapshot snapshot, int caret,
            out int start, out int end)
        {
            start = end = caret;
            if (snapshot == null || snapshot.Length == 0) return false;
            int at = Math.Max(0, Math.Min(caret, snapshot.Length - 1));
            if (!IsIdentifier(snapshot[at]) && at > 0 && IsIdentifier(snapshot[at - 1]))
                at--;
            if (!IsIdentifier(snapshot[at])) return false;
            start = at;
            end = at + 1;
            while (start > 0 && IsIdentifier(snapshot[start - 1])) start--;
            while (end < snapshot.Length && IsIdentifier(snapshot[end])) end++;
            return true;
        }

        private static bool IsIdentifier(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == '$';
        }

        private static string Label(JavaEditorOperation operation)
        {
            switch (operation)
            {
                case JavaEditorOperation.ExtractMethod: return "Estrai metodo";
                case JavaEditorOperation.ExtractVariable: return "Estrai variabile";
                case JavaEditorOperation.ExtractConstant: return "Estrai costante";
                case JavaEditorOperation.InlineVariable: return "Inline variabile";
                default: return "Refactoring";
            }
        }

        internal static bool IsMatching(JObject action, JavaEditorOperation operation)
        {
            if (action == null) return false;
            string title = ((string)action["title"] ?? "").ToLowerInvariant();
            string kind = ((string)action["kind"] ?? "").ToLowerInvariant();
            // A concrete action kind is more reliable than a translated title;
            // however JDT LS may omit 'kind' for legacy Command suggestions.
            switch (operation)
            {
                case JavaEditorOperation.ExtractMethod:
                    return kind.StartsWith("refactor.extract.method", StringComparison.Ordinal) ||
                        kind.StartsWith("refactor.extract.function", StringComparison.Ordinal) ||
                        ContainsAny(title, "extract method", "extract to method", "extract function",
                            "estrai metodo", "estrai in metodo");
                case JavaEditorOperation.ExtractVariable:
                    return kind.StartsWith("refactor.extract.variable", StringComparison.Ordinal) ||
                        (ContainsAny(title, "extract variable", "extract local variable",
                            "extract to local variable", "introduce local variable",
                            "estrai variabile", "introduci variabile") &&
                            !ContainsAny(title, "constant", "costante", "field", "campo"));
                case JavaEditorOperation.ExtractConstant:
                    return kind.StartsWith("refactor.extract.constant", StringComparison.Ordinal) ||
                        ContainsAny(title, "extract constant", "introduce constant",
                            "extract to constant", "estrai costante", "introduci costante");
                case JavaEditorOperation.InlineVariable:
                    // Standard LSP may send just 'refactor.inline', and JDT LS
                    // may title it "Inline 'message'" rather than "Inline variable".
                    // Never select a method/field/constant inline action here.
                    bool otherKind = ContainsAny(title, "method", "function", "metodo",
                        "constant", "costante", "field", "campo");
                    // Generic refactor.inline alone is ambiguous: JDT LS may
                    // mean a method, field or local. Require a local-variable
                    // hint rather than accidentally inlining an unrelated symbol.
                    bool localName = ContainsAny(title,
                        "inline variable", "inline local variable",
                        "inline variabile", "incorpora variabile", "inline local",
                        "inline temporary", "inline temp", "inline '", "inline \"");
                    return !otherKind && (
                        kind.StartsWith("refactor.inline.variable", StringComparison.Ordinal) ||
                        localName);
                default: return false;
            }
        }

        private static bool ContainsAny(string input, params string[] values)
        {
            return values.Any(word => input.IndexOf(word, StringComparison.Ordinal) >= 0);
        }

        private static async Task NotifyAsync(string message, CancellationToken token)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            System.Windows.MessageBox.Show(message, "Java: Refactoring",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }

        private static void Log(Action<string> log, string message)
        {
            if (log != null) log(message);
        }
    }
}
