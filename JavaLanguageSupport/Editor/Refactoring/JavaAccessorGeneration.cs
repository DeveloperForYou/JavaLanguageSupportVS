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
    /// User-selectable getter/setter generation with Eclipse JDT LS.
    /// This class never fabricates Java code: it forwards selected AccessorField
    /// objects back to java/generateAccessors and applies the returned WorkspaceEdit.
    /// </summary>
    internal static class JavaAccessorGeneration
    {
        private sealed class Row
        {
            internal JObject Source;
            internal CheckBox Getter;
            internal CheckBox Setter;
        }

        internal static async Task ExecuteAsync(string uri, ITextBuffer buffer,
            ITextSnapshot snapshot, int caret, Action<string> log, CancellationToken token)
        {
            if (!JavaLspRpc.Connected || buffer == null || snapshot == null ||
                !ReferenceEquals(buffer.CurrentSnapshot, snapshot)) return;

            int position = Math.Max(0, Math.Min(caret, snapshot.Length));
            var line = snapshot.GetLineFromPosition(position);
            JArray diagnostics = JavaDiagnosticsMonitor.ForPosition(uri, line.LineNumber,
                position - line.Start.Position);
            JObject context = JObject.FromObject(new
            {
                textDocument = new { uri },
                range = new
                {
                    start = JavaLspRpc.Position(snapshot, position),
                    end = JavaLspRpc.Position(snapshot, position)
                },
                context = new { diagnostics }
            });
            // AccessorKind is JDT LS's Java enum (GETTER, SETTER, BOTH).
            JObject discovery = (JObject)context.DeepClone();
            discovery["kind"] = "BOTH";
            JToken response = await JavaLspRpc.RequestAsync(
                "java/resolveUnimplementedAccessors", discovery, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            JArray candidates = response as JArray;
            if (candidates == null)
            {
                if (log != null) log("Java accessor: risposta non valida dal server; " +
                    "controlla l'indicizzazione e la versione JDT LS.");
                await ShowNotice("Non sono disponibili i dati per generare getter/setter. " +
                    "Attendi la fine dell'indicizzazione e riprova.", token);
                return;
            }
            if (candidates.Count == 0)
            {
                await ShowNotice("Nessun getter o setter da generare nella classe corrente.", token);
                return;
            }
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            if (!ReferenceEquals(buffer.CurrentSnapshot, snapshot))
            {
                if (log != null) log("Java accessor: file modificato durante la richiesta; ripeti il comando.");
                return;
            }

            JArray selected = Choose(candidates);
            if (selected == null) return;
            if (selected.Count == 0)
            {
                await ShowNotice("Seleziona almeno un getter oppure un setter.", token);
                return;
            }
            if (!ReferenceEquals(buffer.CurrentSnapshot, snapshot))
            {
                if (log != null) log("Java accessor: file modificato dopo la selezione; ripeti il comando.");
                return;
            }

            JObject request = new JObject
            {
                ["context"] = context,
                ["accessors"] = selected
            };
            JToken result = await JavaLspRpc.RequestAsync("java/generateAccessors",
                request, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            JObject edit = result as JObject;
            if (edit == null || (edit["changes"] == null && edit["documentChanges"] == null))
            {
                if (log != null) log("Java accessor: JDT LS non ha restituito WorkspaceEdit.");
                await ShowNotice("JDT LS non ha restituito modifiche applicabili. " +
                    "Controlla i campi selezionati e riprova.", token);
                return;
            }
            if (!ReferenceEquals(buffer.CurrentSnapshot, snapshot))
            {
                if (log != null) log("Java accessor: file modificato durante la generazione; annullato.");
                return;
            }
            JObject action = new JObject
            {
                ["title"] = "Genera getter/setter (selezione campi)",
                ["edit"] = edit.DeepClone()
            };
            await JavaEditorLspOperations.ApplySuggestedCodeActionAsync(action,
                buffer, snapshot, uri, log, token);
        }

        private static async Task ShowNotice(string message, CancellationToken token)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            MessageBox.Show(message, "Java: Genera getter/setter",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private sealed class AccessorRow
        {
            internal JObject Source;
            public string Name { get; set; }
            public string Type { get; set; }
            public bool CanGetter { get; set; }
            public bool CanSetter { get; set; }
            public bool GenerateGetter { get; set; }
            public bool GenerateSetter { get; set; }
        }

        private static JArray Choose(JArray candidates)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var rows = new List<AccessorRow>();
            // DataGrid virtualizes rows and scrolls within the available dialog space.
            // No arbitrary 250-field limit and no fixed-height row stack.
            foreach (JObject item in candidates.OfType<JObject>())
            {
                string fieldName = (string)item["fieldName"];
                if (string.IsNullOrWhiteSpace(fieldName)) continue;
                bool getter = (bool?)item["generateGetter"] == true;
                bool setter = (bool?)item["generateSetter"] == true;
                if (!getter && !setter) continue;
                rows.Add(new AccessorRow
                {
                    Source = (JObject)item.DeepClone(),
                    Name = fieldName,
                    Type = (string)item["typeName"] ?? "?",
                    CanGetter = getter, CanSetter = setter,
                    GenerateGetter = getter, GenerateSetter = setter
                });
            }
            if (rows.Count == 0) return new JArray();
            var table = JavaGenerationDialogStyle.Table();
            JavaGenerationDialogStyle.Column(table, "Nome variabile", "Name", 2.3);
            JavaGenerationDialogStyle.Column(table, "Tipo", "Type", 1.6);
            JavaGenerationDialogStyle.CheckColumn(table, "Getter", "GenerateGetter", "CanGetter");
            JavaGenerationDialogStyle.CheckColumn(table, "Setter", "GenerateSetter", "CanSetter");
            table.ItemsSource = rows;

            var body = new Grid { Margin = new Thickness(13, 10, 13, 0), MinHeight = 0 };
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var note = JavaGenerationDialogStyle.Text(
                "Scegli i campi e i metodi da generare. Le caselle disabilitate indicano " +
                "getter/setter gia' presenti o non consentiti (es. campo final).");
            Grid.SetRow(note, 0); body.Children.Add(note);
            Grid.SetRow(table, 1); body.Children.Add(table);
            if (!JavaGenerationDialogStyle.Show("Java: Genera getter/setter", body))
                return null;

            var result = new JArray();
            foreach (AccessorRow row in rows)
            {
                bool getter = row.CanGetter && row.GenerateGetter;
                bool setter = row.CanSetter && row.GenerateSetter;
                if (!getter && !setter) continue;
                JObject data = (JObject)row.Source.DeepClone();
                data["generateGetter"] = getter;
                data["generateSetter"] = setter;
                result.Add(data);
            }
            return result;
        }

    }
}
