using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Newtonsoft.Json.Linq;

namespace JavaLanguageSupport.Editor.Refactoring
{
    /// <summary>
    /// JDT LS's interactive generation protocol. Unlike ordinary Code Actions,
    /// constructors and overrides are discovered and applied using dedicated
    /// java/* requests. All actual source modifications are produced by JDT LS
    /// and applied by Pack 4's existing validated WorkspaceEdit path.
    /// </summary>
    internal static class JavaMemberGeneration
    {
        internal static async Task ExecuteAsync(JavaEditorOperation operation,
            string uri, ITextBuffer buffer, ITextSnapshot snapshot,
            int caret, Action<string> log, CancellationToken token)
        {
            if (operation != JavaEditorOperation.GenerateConstructor &&
                operation != JavaEditorOperation.OverrideImplementMethods) return;
            if (snapshot == null || buffer == null ||
                !ReferenceEquals(buffer.CurrentSnapshot, snapshot))
            {
                Report(log, "Il documento e' cambiato; ripeti il comando.");
                return;
            }

            int position = Math.Max(0, Math.Min(caret, snapshot.Length));
            var line = snapshot.GetLineFromPosition(position);
            var diagnostics = JavaDiagnosticsMonitor.ForPosition(uri, line.LineNumber,
                position - line.Start.Position);
            JObject context = JObject.FromObject(new {
                textDocument = new { uri },
                range = new {
                    start = JavaLspRpc.Position(snapshot, position),
                    end = JavaLspRpc.Position(snapshot, position)
                },
                context = new { diagnostics }
            });
            string queryMethod = operation == JavaEditorOperation.GenerateConstructor
                ? "java/checkConstructorsStatus" : "java/listOverridableMethods";
            JObject status = await JavaLspRpc.RequestAsync(queryMethod,
                context, token).ConfigureAwait(false) as JObject;
            token.ThrowIfCancellationRequested();
            if (status == null)
            {
                Report(log, queryMethod + " non ha restituito dati. Attendi la fine dell'indicizzazione " +
                    "oppure controlla il supporto della versione JDT LS.");
                await NotifyAsync("JDT LS non ha restituito le opzioni di generazione. " +
                    "Verifica l'indicizzazione e che il cursore si trovi nella classe Java.", token);
                return;
            }

            JObject request;
            string applyMethod;
            string actionName;
            if (operation == JavaEditorOperation.GenerateConstructor)
            {
                var constructors = status["constructors"] as JArray;
                var fields = status["fields"] as JArray;
                if (constructors == null || constructors.Count == 0)
                {
                    Report(log, "Nessun costruttore base disponibile nella classe selezionata.");
                    await NotifyAsync("Nessun costruttore generabile per questa classe. " +
                        "Posiziona il cursore nella dichiarazione di una classe concreta.", token);
                    return;
                }
                JArray chosenConstructors, chosenFields;
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                if (!ReferenceEquals(buffer.CurrentSnapshot, snapshot))
                {
                    Report(log, "Il documento e' cambiato durante la richiesta.");
                    return;
                }
                if (!ChooseConstructors(constructors, fields ?? new JArray(),
                    out chosenConstructors, out chosenFields)) return;
                string duplicateReason;
                if (!JavaConstructorDuplicateGuard.Allowed(snapshot,
                    chosenConstructors, chosenFields, out duplicateReason))
                {
                    await NotifyAsync(duplicateReason ??
                        "La firma selezionata non puo' essere generata in sicurezza.", token);
                    return;
                }
                request = new JObject {
                    ["context"] = context,
                    ["constructors"] = chosenConstructors,
                    ["fields"] = chosenFields
                };
                applyMethod = "java/generateConstructors";
                actionName = "Genera costruttore";
            }
            else
            {
                var methods = status["methods"] as JArray;
                if (methods == null || methods.Count == 0)
                {
                    Report(log, "Nessun metodo ereditato sovrascrivibile nella classe selezionata.");
                    await NotifyAsync("Non sono presenti metodi da implementare o ridefinire " +
                        "in questa classe.", token);
                    return;
                }
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                if (!ReferenceEquals(buffer.CurrentSnapshot, snapshot))
                {
                    Report(log, "Il documento e' cambiato durante la richiesta.");
                    return;
                }
                JArray chosenMethods;
                if (!ChooseOverrides(methods, out chosenMethods)) return;
                request = new JObject {
                    ["context"] = context,
                    ["overridableMethods"] = chosenMethods
                };
                applyMethod = "java/addOverridableMethods";
                actionName = "Override/Implement metodi";
            }

            if (!ReferenceEquals(buffer.CurrentSnapshot, snapshot))
            {
                Report(log, "Il file e' cambiato prima della generazione.");
                return;
            }
            JToken result = await JavaLspRpc.RequestAsync(applyMethod, request,
                token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!(result is JObject) ||
                (((JObject)result)["changes"] == null &&
                 ((JObject)result)["documentChanges"] == null))
            {
                Report(log, applyMethod + " non ha restituito un WorkspaceEdit applicabile.");
                await NotifyAsync("JDT LS non ha generato modifiche. Verifica la scelta e " +
                    "che il codice non contenga gia' i membri richiesti.", token);
                return;
            }
            // Reuse Pack 4 (including its multi-file preview), not custom text edits.
            // The code-action executor checks the exact original document snapshot.
            var generated = new JObject {
                ["title"] = actionName,
                ["edit"] = result.DeepClone()
            };
            await JavaEditorLspOperations.ApplySuggestedCodeActionAsync(generated,
                buffer, snapshot, uri, log, token);
        }

        private static async Task NotifyAsync(string message, CancellationToken token)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            MessageBox.Show(message, "Java: Generazione codice",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static void Report(Action<string> log, string message)
        {
            if (log != null) log("Java generation: " + message);
        }

        private sealed class ConstructorRow
        {
            internal JObject Source;
            public string Name { get; set; }
            public string Parameters { get; set; }
            public bool CanInclude { get { return true; } }
            public bool Include { get; set; }
        }

        private sealed class ConstructorFieldRow
        {
            internal JObject Source;
            public string Name { get; set; }
            public string Type { get; set; }
            public bool CanInclude { get { return true; } }
            public bool Include { get; set; }
        }

        private static bool ChooseConstructors(JArray constructors, JArray fields,
            out JArray chosenConstructors, out JArray chosenFields)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            chosenConstructors = new JArray();
            chosenFields = new JArray();
            var superRows = new List<ConstructorRow>();
            foreach (JObject item in constructors.OfType<JObject>())
                superRows.Add(new ConstructorRow
                {
                    Source = (JObject)item.DeepClone(),
                    Name = (string)item["name"] ?? "Costruttore base",
                    Parameters = Parameters(item),
                    // Keep a useful default but never silently select every overload.
                    Include = superRows.Count == 0
                });
            var fieldRows = new List<ConstructorFieldRow>();
            foreach (JObject item in fields.OfType<JObject>())
                fieldRows.Add(new ConstructorFieldRow
                {
                    Source = (JObject)item.DeepClone(),
                    Name = (string)item["name"] ?? "campo",
                    Type = (string)item["type"] ?? "?",
                    Include = true // All eligible fields can be unchecked individually.
                });
            if (superRows.Count == 0) return false;
            var superTable = JavaGenerationDialogStyle.Table();
            JavaGenerationDialogStyle.Column(superTable, "Costruttore superclasse", "Name", 1.5);
            JavaGenerationDialogStyle.Column(superTable, "Parametri", "Parameters", 2);
            JavaGenerationDialogStyle.CheckColumn(superTable, "Includi", "Include", "CanInclude");
            superTable.ItemsSource = superRows;
            var fieldTable = JavaGenerationDialogStyle.Table();
            JavaGenerationDialogStyle.Column(fieldTable, "Nome variabile", "Name", 2);
            JavaGenerationDialogStyle.Column(fieldTable, "Tipo", "Type", 1.4);
            JavaGenerationDialogStyle.CheckColumn(fieldTable, "Includi", "Include", "CanInclude");
            fieldTable.ItemsSource = fieldRows;

            var body = new Grid { Margin = new Thickness(13, 10, 13, 0), MinHeight = 0 };
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(135) });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var first = JavaGenerationDialogStyle.Text(
                "Costruttori della superclasse: scegli uno o piu' overload da richiamare.", true);
            Grid.SetRow(first, 0); body.Children.Add(first);
            Grid.SetRow(superTable, 1); body.Children.Add(superTable);
            var second = JavaGenerationDialogStyle.Text(
                "Campi da inizializzare: deseleziona quelli da escludere.", true);
            second.Margin = new Thickness(0, 12, 0, 7);
            Grid.SetRow(second, 2); body.Children.Add(second);
            Grid.SetRow(fieldTable, 3); body.Children.Add(fieldTable);
            // Object() is implicit and can be auto-selected. For any other
            // superclass the user must explicitly keep at least one overload.
            bool onlyObject = superRows.Count == 1 &&
                string.Equals(superRows[0].Name, "Object", StringComparison.Ordinal);
            if (!JavaGenerationDialogStyle.Show("Java: Genera costruttore", body,
                790, 580, () =>
                {
                    if (onlyObject || superRows.Any(x => x.Include)) return true;
                    MessageBox.Show("Seleziona almeno un costruttore della superclasse.",
                        "Java: Genera costruttore", MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return false;
                })) return false;
            if (onlyObject && !superRows.Any(x => x.Include))
                superRows[0].Include = true;

            foreach (ConstructorRow row in superRows)
                if (row.Include) chosenConstructors.Add(row.Source.DeepClone());
            foreach (ConstructorFieldRow row in fieldRows)
                if (row.Include) chosenFields.Add(row.Source.DeepClone());
            if (chosenConstructors.Count == 0)
            {
                MessageBox.Show("Seleziona almeno un costruttore della superclasse.",
                    "Java: Genera costruttore", MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return false;
            }
            return true;
        }

        // JDT LS supplies the authoritative list and method metadata. We pass
        // back the original JSON payloads selected by the user, unchanged.
        private sealed class OverrideRow
        {
            internal JObject Source;
            public string MethodName { get; set; }
            public string DeclaringClass { get; set; }
            public bool CanInclude { get { return true; } }
            public bool Include { get; set; }
        }

        private static bool ChooseOverrides(JArray methods, out JArray chosenMethods)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            chosenMethods = new JArray();
            var rows = new List<OverrideRow>();
            foreach (JObject method in methods.OfType<JObject>())
            {
                string sourceType = (string)method["declaringClass"] ?? "(tipo sconosciuto)";
                // JDT LS versions may report either fully-qualified or
                // simple Object. Never preselect those common helper methods.
                bool fromJavaObject = string.Equals(sourceType.Trim(), "java.lang.Object",
                    StringComparison.Ordinal) ||
                    string.Equals(sourceType.Trim(), "Object", StringComparison.Ordinal);
                rows.Add(new OverrideRow
                {
                    Source = (JObject)method.DeepClone(),
                    MethodName = ((string)method["name"] ?? "metodo") + Parameters(method),
                    DeclaringClass = sourceType,
                    // By default select inherited/interface methods, never the
                    // ubiquitous java.lang.Object helpers (equals, clone, etc.).
                    // The user can still explicitly select Object methods.
                    Include = !fromJavaObject
                });
            }
            if (rows.Count == 0) return false;

            var table = JavaGenerationDialogStyle.Table();
            JavaGenerationDialogStyle.Column(table, "Nome metodo", "MethodName", 2.4);
            JavaGenerationDialogStyle.Column(table, "Classe di provenienza", "DeclaringClass", 2.0);
            JavaGenerationDialogStyle.CheckColumn(table, "Includi", "Include", "CanInclude");
            table.ItemsSource = rows;

            var body = new Grid { Margin = new Thickness(13, 10, 13, 0), MinHeight = 0 };
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition {
                Height = new GridLength(1, GridUnitType.Star)
            });
            var label = JavaGenerationDialogStyle.Text(
                "Seleziona i metodi da generare. Per impostazione predefinita " +
                "sono inclusi i metodi delle superclassi e delle interfacce " +
                "(escluso java.lang.Object).", true);
            Grid.SetRow(label, 0); body.Children.Add(label);
            Grid.SetRow(table, 1); body.Children.Add(table);
            // D1: keep the modal open if every method is unchecked.
            if (!JavaGenerationDialogStyle.Show(
                "Java: Override/Implement metodi", body, 900, 580, () =>
                {
                    if (rows.Any(r => r.Include)) return true;
                    MessageBox.Show("Seleziona almeno un metodo da generare.",
                        "Java: Override/Implement", MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return false;
                })) return false;

            foreach (OverrideRow row in rows)
                if (row.Include) chosenMethods.Add(row.Source.DeepClone());
            if (chosenMethods.Count == 0)
            {
                MessageBox.Show("Seleziona almeno un metodo da implementare o ridefinire.",
                    "Java: Override/Implement", MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return false;
            }
            return true;
        }

        private static string Parameters(JToken value)
        {
            var parameters = value["parameters"] as JArray;
            if (parameters == null || parameters.Count == 0) return "()";
            return "(" + string.Join(", ", parameters.Select(x => (string)x ?? "?")) + ")";
        }

    }
}
