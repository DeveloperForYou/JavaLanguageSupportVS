using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Newtonsoft.Json.Linq;

namespace JavaLanguageSupport.Editor.Refactoring
{
    /// <summary>
    /// Explicit Java refactoring/code-generation actions selected by the user.
    /// All transformations come from the active Eclipse JDT LS. We do not infer
    /// Java syntax or generate code in the VSIX. Applying an action delegates
    /// to Pack 4's validated WorkspaceEdit implementation.
    /// </summary>
    internal static class JavaAdvancedRefactorings
    {
        internal static bool Supports(JavaEditorOperation operation)
        {
            return operation == JavaEditorOperation.ChangeMethodSignature ||
                operation == JavaEditorOperation.GenerateConstructor ||
                operation == JavaEditorOperation.GenerateGettersSetters ||
                operation == JavaEditorOperation.OverrideImplementMethods;
        }

        internal static async Task ExecuteAsync(JavaEditorOperation operation,
            string uri, ITextBuffer buffer, ITextSnapshot snapshot, SnapshotSpan selection,
            int caret, Action<string> log, CancellationToken token)
        {
            if (!Supports(operation)) return;
            if (buffer == null || snapshot == null || selection.Snapshot != snapshot ||
                !ReferenceEquals(buffer.CurrentSnapshot, snapshot))
            {
                Report(log, "Il documento Java e' stato modificato. Ripeti il comando.");
                return;
            }

            // Constructors/override generation is a JDT LS interactive protocol,
            // not a regular textDocument/codeAction. Do not filter by titles.
            if (operation == JavaEditorOperation.GenerateConstructor ||
                operation == JavaEditorOperation.OverrideImplementMethods)
            {
                await JavaMemberGeneration.ExecuteAsync(operation, uri, buffer, snapshot,
                    caret, log, token);
                return;
            }

            int position = Math.Max(0, Math.Min(caret, snapshot.Length));
            ITextSnapshotLine line = snapshot.GetLineFromPosition(position);
            JArray diagnostics = JavaDiagnosticsMonitor.ForPosition(uri, line.LineNumber,
                position - line.Start.Position);

            // By default, query at the caret: signature/constructor/override
            // actions usually belong to a method or class declaration. A real
            // selection is queried first when supplied by the user.
            int start = selection.IsEmpty ? position : selection.Start.Position;
            int end = selection.IsEmpty ? position : selection.End.Position;
            JArray actions = await QueryAsync(uri, snapshot, start, end,
                diagnostics, token).ConfigureAwait(false);
            var matching = Filter(actions, operation);

            // Some JDT LS versions only return generation actions for a caret
            // position; retry without a selection if necessary.
            if (matching.Count == 0 && !selection.IsEmpty)
            {
                JArray atCaret = await QueryAsync(uri, snapshot, position, position,
                    diagnostics, token).ConfigureAwait(false);
                matching = Filter(atCaret, operation);
                if (atCaret != null && atCaret.Count != 0) actions = atCaret;
            }

            token.ThrowIfCancellationRequested();
            if (matching.Count == 0)
            {
                if (log != null)
                {
                    var titles = actions == null ? new List<string>() : actions
                        .OfType<JObject>().Take(30).Select(a =>
                            ((string)a["title"] ?? "(senza titolo)") + " [" +
                            ((string)a["kind"] ?? "nessun kind") + "]").ToList();
                    Report(log, "Java Pack 6: nessuna azione compatibile con '" +
                        Name(operation) + "'. JDT LS ha proposto: " +
                        (titles.Count == 0 ? "nessuna" : string.Join(" | ", titles)));
                }
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                System.Windows.MessageBox.Show(
                    "JDT LS non propone '" + Name(operation) +
                    "' in questa posizione. Attendi l'indicizzazione del progetto " +
                    "e posiziona il cursore sul metodo o sulla classe interessata. " +
                    "Alcune versioni di JDT LS non espongono questa operazione " +
                    "come Code Action.",
                    "Java: refactoring", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            if (!ReferenceEquals(buffer.CurrentSnapshot, snapshot))
            {
                Report(log, "Il sorgente e' cambiato durante la richiesta. Riprova.");
                return;
            }
            JObject chosen = matching.Count == 1
                ? matching[0] : JavaEditorDialogs.Choose(matching);
            if (chosen == null) return;
            await JavaEditorLspOperations.ApplySuggestedCodeActionAsync(chosen,
                buffer, snapshot, uri, log, token);
        }

        private static async Task<JArray> QueryAsync(string uri, ITextSnapshot snapshot,
            int start, int end, JArray diagnostics, CancellationToken token)
        {
            object request = new
            {
                textDocument = new { uri },
                range = new
                {
                    start = JavaLspRpc.Position(snapshot, start),
                    end = JavaLspRpc.Position(snapshot, end)
                },
                context = new { diagnostics }
            };
            JToken reply = await JavaLspRpc.RequestAsync(
                "textDocument/codeAction", request, token).ConfigureAwait(false);
            return reply as JArray;
        }

        private static List<JObject> Filter(JArray actions,
            JavaEditorOperation operation)
        {
            if (actions == null) return new List<JObject>();
            return actions.OfType<JObject>()
                .Where(a => Matches(a, operation))
                .GroupBy(a => (string)a["title"] ?? "", StringComparer.Ordinal)
                .Select(g => g.First())
                .Take(40).ToList();
        }

        internal static bool Matches(JObject action, JavaEditorOperation operation)
        {
            if (action == null) return false;
            string title = ((string)action["title"] ?? "").ToLowerInvariant();
            string kind = ((string)action["kind"] ?? "").ToLowerInvariant();
            string command = ((string)action.SelectToken("command.command") ?? "").ToLowerInvariant();
            switch (operation)
            {
                case JavaEditorOperation.ChangeMethodSignature:
                    return Starts(kind, "refactor.change.signature", "refactor.changeSignature") ||
                        Contains(title, "change method signature", "change signature",
                            "cambia firma", "modifica firma") ||
                        Contains(command, "changesignature");
                case JavaEditorOperation.GenerateConstructor:
                    return Starts(kind, "source.generateconstructors", "source.generate.constructor") ||
                        Contains(title, "generate constructor", "generate constructors",
                            "create constructor", "genera costruttor", "crea costruttor") ||
                        Contains(command, "generateconstructors");
                case JavaEditorOperation.GenerateGettersSetters:
                    return Starts(kind, "source.generateaccessors", "source.generate.getter",
                            "source.generate.setter") ||
                        Contains(title, "generate getter", "generate setter",
                            "generate accessor", "getter and setter", "getters and setters",
                            "getter/setter", "genera getter", "genera setter") ||
                        Contains(command, "generateaccessors", "generatesetter", "generategetter");
                case JavaEditorOperation.OverrideImplementMethods:
                    return Starts(kind, "source.overridemethods", "source.implementmethods") ||
                        Contains(title, "override/implement", "override methods",
                            "override method", "implement methods", "implement method",
                            "add unimplemented methods", "implementa metodi",
                            "sovrascrivi metodi", "ridefinisci metodi") ||
                        Contains(command, "overridemethods", "implementmethods");
                default:
                    return false;
            }
        }

        private static bool Starts(string value, params string[] options)
        {
            foreach (string option in options)
                if (value.StartsWith(option.ToLowerInvariant(), StringComparison.Ordinal))
                    return true;
            return false;
        }

        private static bool Contains(string value, params string[] options)
        {
            foreach (string option in options)
                if (value.IndexOf(option, StringComparison.Ordinal) >= 0)
                    return true;
            return false;
        }

        private static string Name(JavaEditorOperation operation)
        {
            switch (operation)
            {
                case JavaEditorOperation.ChangeMethodSignature: return "Cambia firma metodo";
                case JavaEditorOperation.GenerateConstructor: return "Genera costruttore";
                case JavaEditorOperation.GenerateGettersSetters: return "Genera getter/setter";
                case JavaEditorOperation.OverrideImplementMethods: return "Override/Implement metodi";
                default: return "Refactoring";
            }
        }

        private static void Report(Action<string> log, string message)
        {
            if (log != null) log(message);
        }
    }
}
