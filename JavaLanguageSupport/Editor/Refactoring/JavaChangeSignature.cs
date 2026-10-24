using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.VisualStudio.PlatformUI;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Newtonsoft.Json.Linq;

// PACK C3: refactoring robustness and signature validation.
namespace JavaLanguageSupport.Editor.Refactoring
{
    /// <summary>Interactive JDT LS change-signature protocol; the server owns all edits.</summary>
    internal static class JavaChangeSignature
    {
        private sealed class ParameterRow
        {
            public string Type { get; set; }
            public string Name { get; set; }
            public string DefaultValue { get; set; }
            public int OriginalIndex { get; set; }
        }

        internal static async Task ExecuteAsync(string uri, ITextBuffer buffer,
            ITextSnapshot snapshot, int caret, Action<string> log, CancellationToken token)
        {
            int point = Math.Max(0, Math.Min(caret, snapshot.Length));
            var line = snapshot.GetLineFromPosition(point);
            JObject context = JObject.FromObject(new {
                textDocument = new { uri },
                range = new { start = JavaLspRpc.Position(snapshot, point),
                    end = JavaLspRpc.Position(snapshot, point) },
                context = new { diagnostics = JavaDiagnosticsMonitor.ForPosition(uri,
                    line.LineNumber, point - line.Start.Position) }
            });
            var info = await JavaLspRpc.RequestAsync("java/getChangeSignatureInfo",
                context, token).ConfigureAwait(false) as JObject;
            token.ThrowIfCancellationRequested();
            if (info == null || string.IsNullOrWhiteSpace((string)info["methodIdentifier"]))
            {
                await Notice("Seleziona la dichiarazione di un metodo modificabile. " +
                    ((string)info?["errorMessage"] ?? "JDT LS non ha restituito la firma."), token);
                return;
            }
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            if (!ReferenceEquals(buffer.CurrentSnapshot, snapshot)) return;
            JObject updated;
            if (!Choose(info, out updated)) return;
            if (!ReferenceEquals(buffer.CurrentSnapshot, snapshot)) return;

            // GetRefactorEditHandler expects exactly eight arguments in this order.
            var commandArguments = new JArray {
                info["methodIdentifier"].DeepClone(),
                false, // no automatically kept deprecated delegate method
                updated["methodName"], updated["modifier"],
                updated["returnType"], updated["parameters"],
                updated["exceptions"], true
            };
            var request = new JObject {
                ["command"] = "changeSignature",
                ["context"] = context,
                ["commandArguments"] = commandArguments
            };
            var response = await JavaLspRpc.RequestAsync("java/getRefactorEdit",
                request, token).ConfigureAwait(false) as JObject;
            token.ThrowIfCancellationRequested();
            if (response == null || response["edit"] == null ||
                (response["edit"]["changes"] == null &&
                 response["edit"]["documentChanges"] == null))
            {
                await Notice("JDT LS non ha prodotto una modifica della firma: " +
                    ((string)response?["errorMessage"] ?? "controlla parametri e tipi."), token);
                return;
            }
            if (!ReferenceEquals(buffer.CurrentSnapshot, snapshot)) return;
            var action = new JObject {
                ["title"] = "Java: Cambia firma metodo",
                ["edit"] = response["edit"].DeepClone()
            };
            await JavaEditorLspOperations.ApplySuggestedCodeActionAsync(action,
                buffer, snapshot, uri, log, token);
        }

        private static bool Choose(JObject info, out JObject updated)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            updated = null;
            var name = new TextBox { Text = (string)info["methodName"] ?? "" };
            var returnType = new TextBox { Text = (string)info["returnType"] ?? "void" };
            var modifier = new ComboBox { IsEditable = false, MinWidth = 135 };
            modifier.SetResourceReference(FrameworkElement.StyleProperty,
                VsResourceKeys.ComboBoxStyleKey);
            modifier.SetResourceReference(ItemsControl.ItemContainerStyleProperty,
                VsResourceKeys.ComboBoxItemStyleKey);
            modifier.Resources[SystemColors.HighlightTextBrushKey] =
                System.Windows.Media.Brushes.White;
            foreach (string item in new [] { "public", "protected", "private", "default" })
                modifier.Items.Add(item);
            string current = (string)info["modifier"] ?? "public";
            modifier.SelectedItem = modifier.Items.Cast<string>().FirstOrDefault(x => x == current)
                ?? "public";
            foreach (var c in new Control[] { name, returnType, modifier })
            {
                c.Margin = new Thickness(3, 0, 8, 5);
                // ComboBox receives its Visual Studio control template above;
                // text boxes use the VS toolwindow palette directly.
                if (ReferenceEquals(c, modifier)) continue;
                c.SetResourceReference(Control.BackgroundProperty,
                    EnvironmentColors.ToolWindowBackgroundBrushKey);
                c.SetResourceReference(Control.ForegroundProperty,
                    EnvironmentColors.ToolWindowTextBrushKey);
            }
            // Type suggestions are backed by JDT LS workspace/symbol, debounced.
            // The value field offers context-appropriate Java literal templates.
            JavaSignatureInputCompletion.Attach(returnType, false);
            var rows = new System.Collections.ObjectModel.ObservableCollection<ParameterRow>();
            foreach (JObject p in (info["parameters"] as JArray ?? new JArray()).OfType<JObject>())
                rows.Add(new ParameterRow {
                    Type = (string)p["type"] ?? "", Name = (string)p["name"] ?? "",
                    DefaultValue = (string)p["defaultValue"] ?? "null",
                    OriginalIndex = (int?)p["originalIndex"] ?? -1
                });
            var table = JavaGenerationDialogStyle.Table();
            table.SelectionUnit = DataGridSelectionUnit.FullRow;
            table.CanUserSortColumns = false;
            AddEditable(table, "Tipo", "Type", 1.6);
            AddEditable(table, "Nome", "Name", 1.4);
            AddEditable(table, "Valore default (nuovi)", "DefaultValue", 1.7);
            table.ItemsSource = rows;
            table.PreparingCellForEdit += (sender, args) =>
            {
                var box = args.EditingElement as TextBox;
                var column = args.Column as DataGridTextColumn;
                if (box == null || column == null) return;
                string columnName = column.Header as string ?? "";
                if (columnName == "Tipo")
                    JavaSignatureInputCompletion.Attach(box, false);
                else if (columnName.StartsWith("Valore default", StringComparison.Ordinal))
                {
                    var parameter = args.Row.Item as ParameterRow;
                    JavaSignatureInputCompletion.Attach(box, true,
                        () => parameter == null ? "" : parameter.Type);
                }
            };
            // Shared dialog hover behavior also themes these action buttons.
            var add = new Button { Content = "Aggiungi", MinWidth = 88, Margin = new Thickness(4) };
            var remove = new Button { Content = "Rimuovi", MinWidth = 88, Margin = new Thickness(4) };
            var up = new Button { Content = "↑", MinWidth = 36, Margin = new Thickness(4) };
            var down = new Button { Content = "↓", MinWidth = 36, Margin = new Thickness(4) };
            add.Click += (s,e) => {
                // Avoid inserting a duplicate name when the user adds several
                // parameters before changing their types or descriptions.
                string nextName = "parameter";
                int suffix = 2;
                while (rows.Any(x => string.Equals(x.Name, nextName, StringComparison.Ordinal)))
                    nextName = "parameter" + suffix++;
                var row = new ParameterRow { Type = "Object", Name = nextName,
                    DefaultValue = "null", OriginalIndex = -1 };
                rows.Add(row);
                table.SelectedItem = row;
                table.ScrollIntoView(row);
            };
            remove.Click += (s,e) => { var row = table.SelectedItem as ParameterRow;
                if (row != null) rows.Remove(row); };
            up.Click += (s,e) => Move(rows, table, -1);
            down.Click += (s,e) => Move(rows, table, 1);
            var controls = new StackPanel { Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right };
            // Use the same idle colors as the dialog footer buttons.
            // The themed style alone may keep action buttons highlighted even
            // when they no longer have keyboard or mouse focus.
            foreach (Button b in new[] { add, remove, up, down })
            {
                b.SetResourceReference(FrameworkElement.StyleProperty,
                    Microsoft.VisualStudio.Shell.VsResourceKeys.ThemedDialogButtonStyleKey);
                b.SetResourceReference(Control.BackgroundProperty,
                    EnvironmentColors.ToolWindowBackgroundBrushKey);
                b.SetResourceReference(Control.ForegroundProperty,
                    EnvironmentColors.ToolWindowTextBrushKey);
                b.SetResourceReference(Control.BorderBrushProperty,
                    EnvironmentColors.ToolWindowBorderBrushKey);
                controls.Children.Add(b);
            }
            var form = new Grid { Margin = new Thickness(12) };
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            form.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddLabel(form, "Nome metodo", 0); Grid.SetRow(name, 1); form.Children.Add(name);
            var middle = new Grid();
            middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var t1 = JavaGenerationDialogStyle.Text("Tipo restituito");
            var t2 = JavaGenerationDialogStyle.Text("Visibilità");
            middle.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            middle.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(t2, 0); Grid.SetColumn(t2, 1); middle.Children.Add(t2);
            Grid.SetRow(t1, 0); middle.Children.Add(t1);
            Grid.SetRow(returnType, 1); middle.Children.Add(returnType);
            Grid.SetRow(modifier, 1); Grid.SetColumn(modifier, 1); middle.Children.Add(modifier);
            Grid.SetRow(middle, 2); form.Children.Add(middle);
            AddLabel(form, "Parametri (modifica, aggiungi, elimina o riordina)", 3);
            Grid.SetRow(table, 5); form.Children.Add(table);
            Grid.SetRow(controls, 6); form.Children.Add(controls);
            if (!JavaGenerationDialogStyle.Show("Java: Cambia firma metodo", form, 850, 620,
                () => {
                    // Commit active DataGrid cell BEFORE reading the model.
                    // Otherwise clicking Genera while editing may send the
                    // previous value to JDT LS, despite the visible new text.
                    if (!table.CommitEdit(DataGridEditingUnit.Cell, true) ||
                        !table.CommitEdit(DataGridEditingUnit.Row, true))
                        return false;
                    return Validate(name.Text, returnType.Text, rows);
                })) return false;
            var parameters = new JArray();
            foreach (ParameterRow p in rows)
                parameters.Add(new JObject {
                    ["type"] = p.Type.Trim(), ["name"] = p.Name.Trim(),
                    ["defaultValue"] = p.DefaultValue ?? "null",
                    ["originalIndex"] = p.OriginalIndex
                });
            updated = new JObject {
                ["methodName"] = name.Text.Trim(), ["modifier"] = (string)modifier.SelectedItem,
                ["returnType"] = returnType.Text.Trim(), ["parameters"] = parameters,
                ["exceptions"] = info["exceptions"]?.DeepClone() ?? new JArray()
            };
            return true;
        }
        private static void AddLabel(Grid grid, string label, int row)
        {
            var text = JavaGenerationDialogStyle.Text(label);
            Grid.SetRow(text, row); grid.Children.Add(text);
        }
        private static void Move(System.Collections.ObjectModel.ObservableCollection<ParameterRow> rows,
            DataGrid grid, int direction)
        {
            var row = grid.SelectedItem as ParameterRow;
            if (row == null) return;
            int old = rows.IndexOf(row), target = old + direction;
            if (target < 0 || target >= rows.Count) return;
            rows.Move(old, target); grid.SelectedItem = row;
        }
        private static void AddEditable(DataGrid table, string title, string path, double weight)
        {
            table.Columns.Add(new DataGridTextColumn { Header = title,
                Binding = new Binding(path) { Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                Width = new DataGridLength(weight, DataGridLengthUnitType.Star), MinWidth = 100 });
        }
        // Java reserved identifiers are invalid for a method or parameter.
        // Contextual keywords are handled conservatively by JDT LS itself.
        private static readonly HashSet<string> JavaReserved = new HashSet<string>(
            StringComparer.Ordinal) {
                "abstract", "assert", "boolean", "break", "byte", "case", "catch",
                "char", "class", "const", "continue", "default", "do", "double",
                "else", "enum", "extends", "final", "finally", "float", "for",
                "goto", "if", "implements", "import", "instanceof", "int",
                "interface", "long", "native", "new", "package", "private",
                "protected", "public", "return", "short", "static", "strictfp",
                "super", "switch", "synchronized", "this", "throw", "throws",
                "transient", "try", "void", "volatile", "while", "true", "false",
                "null", "_"
            };

        private static bool ValidName(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return false;
            string trimmed = candidate.Trim();
            return !JavaReserved.Contains(trimmed) &&
                System.Text.RegularExpressions.Regex.IsMatch(trimmed,
                    @"^[\p{L}_$][\p{L}\p{N}_$]*$");
        }

        private static bool Validate(string name, string returnType,
            System.Collections.ObjectModel.ObservableCollection<ParameterRow> parameters)
        {
            if (!ValidName(name) || string.IsNullOrWhiteSpace(returnType) ||
                string.Equals(returnType.Trim(), "var", StringComparison.Ordinal))
            {
                MessageBox.Show("Nome del metodo o tipo restituito non valido.",
                    "Java: Cambia firma metodo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (parameters.Any(x => !ValidName(x.Name) || string.IsNullOrWhiteSpace(x.Type)) ||
                parameters.GroupBy(x => x.Name.Trim(), StringComparer.Ordinal)
                    .Any(g => g.Count() > 1))
            {
                MessageBox.Show("Controlla nomi e tipi dei parametri (nomi non duplicati).",
                    "Java: Cambia firma metodo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            return true;
        }
        private static async Task Notice(string value, CancellationToken token)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            MessageBox.Show(value, "Java: Cambia firma metodo", MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }
}
