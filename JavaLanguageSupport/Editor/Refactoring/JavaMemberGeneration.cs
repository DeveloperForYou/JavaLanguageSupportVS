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
        private sealed class Choice
        {
            internal JObject Data;
            internal string Label;
        }

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

        private static bool ChooseConstructors(JArray constructors, JArray fields,
            out JArray chosenConstructors, out JArray chosenFields)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            chosenConstructors = new JArray();
            chosenFields = new JArray();
            if (constructors.Count > 200 || fields.Count > 200)
            {
                MessageBox.Show("Troppi elementi da mostrare (oltre 200). " +
                    "Restringi la classe o i campi selezionabili.",
                    "Java: Genera costruttore", MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return false;
            }
            var constructorList = MakeList(constructors, x =>
                ((string)x["name"] ?? "Costruttore base") + Parameters(x),
                x => false);
            if (constructorList.Items.Count > 0)
                constructorList.SelectedIndex = 0;
            var fieldList = MakeList(fields, x =>
                ((string)x["name"] ?? "campo") + " : " + ((string)x["type"] ?? "?"),
                x => true);

            var form = new StackPanel { Margin = new Thickness(12) };
            form.Children.Add(MakeHeader("Costruttore della superclasse (obbligatorio):"));
            constructorList.Height = 128;
            form.Children.Add(constructorList);
            form.Children.Add(MakeHeader("Campi da inizializzare (Ctrl/clic per selezione multipla):"));
            fieldList.Height = 160;
            form.Children.Add(fieldList);
            bool accepted = ShowSelector("Java: Genera costruttore", form);
            if (!accepted) return false;
            chosenConstructors = Selected(constructorList);
            chosenFields = Selected(fieldList);
            return chosenConstructors.Count > 0;
        }

        private static bool ChooseOverrides(JArray methods, out JArray chosenMethods)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            chosenMethods = new JArray();
            if (methods.Count > 250)
            {
                MessageBox.Show("Troppi metodi ereditati da visualizzare (oltre 250).",
                    "Java: Override/Implement", MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return false;
            }
            var list = MakeList(methods, x =>
                ((string)x["declaringClass"] ?? "tipo base") + " · " +
                ((string)x["name"] ?? "metodo") + Parameters(x) +
                ((bool?)x["unimplemented"] == true ? "  [da implementare]" : ""),
                x => (bool?)x["unimplemented"] == true);
            // If the type has no abstract/unimplemented methods, still allow
            // choosing a concrete inherited method to override.
            if (list.SelectedItems.Count == 0 && list.Items.Count > 0)
                list.SelectedIndex = 0;
            list.Height = 350;
            var form = new StackPanel { Margin = new Thickness(12) };
            form.Children.Add(MakeHeader("Seleziona i metodi da implementare o ridefinire " +
                "(Ctrl/clic per selezione multipla):"));
            form.Children.Add(list);
            if (!ShowSelector("Java: Override/Implement metodi", form)) return false;
            chosenMethods = Selected(list);
            return chosenMethods.Count > 0;
        }

        private static string Parameters(JToken value)
        {
            var parameters = value["parameters"] as JArray;
            if (parameters == null || parameters.Count == 0) return "()";
            return "(" + string.Join(", ", parameters.Select(x => (string)x ?? "?")) + ")";
        }

        private static TextBlock MakeHeader(string value)
        {
            var text = new TextBlock { Text = value, Margin = new Thickness(0, 10, 0, 5),
                TextWrapping = TextWrapping.Wrap };
            text.SetResourceReference(TextBlock.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            return text;
        }

        private static ListBox MakeList(JArray items, Func<JObject, string> label,
            Func<JObject, bool> selectDefault)
        {
            var list = new ListBox {
                SelectionMode = SelectionMode.Extended,
                MinWidth = 580
            };
            list.SetResourceReference(Control.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            list.SetResourceReference(Control.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            list.SetResourceReference(Control.BorderBrushProperty,
                EnvironmentColors.ToolWindowBorderBrushKey);
            foreach (JObject data in items.OfType<JObject>())
            {
                var choice = new Choice { Data = (JObject)data.DeepClone(),
                    Label = label(data) };
                var item = new ListBoxItem { Content = choice.Label, Tag = choice };
                list.Items.Add(item);
                if (selectDefault(data)) item.IsSelected = true;
            }
            return list;
        }

        private static JArray Selected(ListBox list)
        {
            var output = new JArray();
            foreach (ListBoxItem item in list.SelectedItems)
            {
                var choice = item.Tag as Choice;
                if (choice != null) output.Add(choice.Data.DeepClone());
            }
            return output;
        }

        private static bool ShowSelector(string title, FrameworkElement form)
        {
            var window = new Window {
                Title = title, Width = 700, Height = 510,
                MinWidth = 620, MinHeight = 350,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.CanResize,
                ShowInTaskbar = false
            };
            window.SetResourceReference(Window.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            var root = new DockPanel();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(10) };
            var ok = new Button { Content = "Genera", IsDefault = true,
                MinWidth = 96, Margin = new Thickness(4) };
            var cancel = new Button { Content = "Annulla", IsCancel = true,
                MinWidth = 96, Margin = new Thickness(4) };
            foreach (var b in new[] { ok, cancel })
            {
                b.SetResourceReference(Control.ForegroundProperty,
                    EnvironmentColors.ToolWindowTextBrushKey);
                b.SetResourceReference(Control.BackgroundProperty,
                    EnvironmentColors.ToolWindowBackgroundBrushKey);
            }
            ok.Click += (s, e) => { window.DialogResult = true; window.Close(); };
            cancel.Click += (s, e) => { window.DialogResult = false; window.Close(); };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            var scroll = new ScrollViewer { Content = form,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
            root.Children.Add(scroll);
            window.Content = root;
            return window.ShowDialog() == true;
        }
    }
}
