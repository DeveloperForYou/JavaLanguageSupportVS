using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using EnvDTE;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Newtonsoft.Json.Linq;
using Task = System.Threading.Tasks.Task;

namespace JavaLanguageSupport.Editor.Navigation
{
    // Navigazione esplicita. F12 richiede una integrazione separata con il comando nativo VS.
    internal static class JavaNavigation
    {
        internal static async Task ExecuteAsync(JavaLanguageSupport.Editor.Refactoring.JavaEditorOperation operation,
            string documentUri, ITextSnapshot snapshot, int caret, Action<string> log, CancellationToken token)
        {
            JToken result;
            var targets = new List<JavaSymbolDestination>();
            switch (operation)
            {
                case JavaLanguageSupport.Editor.Refactoring.JavaEditorOperation.GoToDefinition:
                    result = await JavaLspRpc.RequestAsync("textDocument/definition",
                        new { textDocument = new { uri = documentUri }, position = JavaLspRpc.Position(snapshot, caret) },
                        token).ConfigureAwait(false);
                    ReadLocations(targets, result, "Definizione");
                    break;
                case JavaLanguageSupport.Editor.Refactoring.JavaEditorOperation.GoToDocumentSymbol:
                    result = await JavaLspRpc.RequestAsync("textDocument/documentSymbol",
                        new { textDocument = new { uri = documentUri } }, token).ConfigureAwait(false);
                    if (result is JArray)
                    {
                        foreach (JToken item in (JArray)result) ReadDocumentSymbol(targets, item, documentUri, 0);
                    }
                    break;
                case JavaLanguageSupport.Editor.Refactoring.JavaEditorOperation.GoToWorkspaceSymbol:
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                    string query = JavaLanguageSupport.Editor.Refactoring.JavaEditorDialogs.AskText(
                        "Cerca simbolo Java", "Nome della classe, del metodo o del campo:");
                    if (string.IsNullOrWhiteSpace(query)) return;
                    result = await JavaLspRpc.RequestAsync("workspace/symbol", new { query }, token)
                        .ConfigureAwait(false);
                    ReadWorkspaceSymbols(targets, result);
                    break;
                default: return;
            }
            token.ThrowIfCancellationRequested();
            if (targets.Count == 0)
            {
                log("JDT LS: nessun simbolo o posizione restituiti. Controlla l'indicizzazione Java.");
                return;
            }
            JavaSymbolDestination chosen = targets[0];
            if (targets.Count > 1 || operation != JavaLanguageSupport.Editor.Refactoring.JavaEditorOperation.GoToDefinition)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                chosen = JavaNavigationDialog.Choose(targets);
            }
            if (chosen == null) return;
            string path = await ResolvePathAsync(chosen.Uri, log, token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(path)) return;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            var dte = Package.GetGlobalService(typeof(Microsoft.VisualStudio.Shell.Interop.SDTE)) as DTE;
            if (dte == null) { log("Servizio DTE di Visual Studio non disponibile."); return; }
            try
            {
                dte.ItemOperations.OpenFile(path, Constants.vsViewKindTextView);
                var selection = dte.ActiveDocument == null ? null : dte.ActiveDocument.Selection as TextSelection;
                if (selection != null)
                    selection.MoveToLineAndOffset(Math.Max(1, chosen.Line + 1),
                        Math.Max(1, chosen.Column + 1), false);
                log("Java: aperto " + path + ":" + (chosen.Line + 1));
            }
            catch (Exception ex) { log("Impossibile aprire la destinazione: " + ex.Message); }
        }

        private static void ReadLocations(List<JavaSymbolDestination> destinations, JToken token, string label)
        {
            if (token == null || token.Type == JTokenType.Null) return;
            if (token is JArray)
            {
                foreach (var item in (JArray)token) ReadLocations(destinations, item, label);
                return;
            }
            string uri = (string)token["targetUri"] ?? (string)token["uri"];
            var range = token["targetSelectionRange"] ?? token["targetRange"] ?? token["range"];
            Add(destinations, label, uri, range);
        }
        private static void ReadDocumentSymbol(List<JavaSymbolDestination> destinations,
            JToken symbol, string uri, int depth)
        {
            if (!(symbol is JObject) || depth > 30) return;
            // DocumentSymbol (nested) o SymbolInformation (flat) sono entrambi previsti da LSP.
            string name = (string)symbol["name"] ?? "Simbolo";
            var location = symbol["location"];
            string symbolUri = (string)location?["uri"] ?? uri;
            JToken range = symbol["selectionRange"] ?? symbol["range"] ?? location?["range"];
            Add(destinations, new string(' ', Math.Min(depth * 2, 40)) + name, symbolUri, range);
            if (symbol["children"] is JArray)
                foreach (var child in (JArray)symbol["children"])
                    ReadDocumentSymbol(destinations, child, uri, depth + 1);
        }
        private static void ReadWorkspaceSymbols(List<JavaSymbolDestination> destinations, JToken response)
        {
            if (!(response is JArray)) return;
            foreach (var item in (JArray)response)
            {
                var location = item["location"];
                string uri = (string)location?["uri"];
                var range = location?["range"];
                string name = (string)item["name"] ?? "Simbolo";
                string container = (string)item["containerName"];
                if (!string.IsNullOrEmpty(container)) name += " — " + container;
                // WorkspaceSymbol può fornire una location senza range: apriamo comunque il file.
                Add(destinations, name, uri, range);
            }
        }
        private static void Add(List<JavaSymbolDestination> results, string label, string uri, JToken range)
        {
            if (string.IsNullOrEmpty(uri)) return;
            JToken start = range?["start"];
            int line = start?["line"]?.Value<int>() ?? 0;
            int column = start?["character"]?.Value<int>() ?? 0;
            results.Add(new JavaSymbolDestination { Name = label, Uri = uri,
                Line = Math.Max(0, line), Column = Math.Max(0, column) });
        }

        private static async Task<string> ResolvePathAsync(string address, Action<string> log,
            CancellationToken token)
        {
            Uri uri;
            if (!Uri.TryCreate(address, UriKind.Absolute, out uri))
            { log("URI restituito da JDT LS non valido: " + address); return null; }
            if (uri.IsFile) return uri.LocalPath;
            if (!string.Equals(uri.Scheme, "jdt", StringComparison.OrdinalIgnoreCase))
            { log("URI non supportato: " + uri.Scheme); return null; }

            // JDT LS recente: LSP 3.18; JDT LS precedente: richiesta proprietaria.
            JToken response = await JavaLspRpc.RequestAsync("workspace/textDocumentContent",
                new { uri = address }, token).ConfigureAwait(false);
            string source = ExtractSource(response);
            if (string.IsNullOrWhiteSpace(source))
            {
                response = await JavaLspRpc.RequestAsync("java/classFileContents",
                    new { uri = address }, token).ConfigureAwait(false);
                source = ExtractSource(response);
            }
            if (string.IsNullOrWhiteSpace(source))
            {
                log("Sorgente JDK non disponibile per " + address +
                    ". Verifica versione JDT LS e sorgenti JDK installati.");
                return null;
            }
            string cacheRoot = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "JavaLanguageSupportVS", "JdkNavigation");
            string display = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
            if (string.IsNullOrWhiteSpace(display)) display = "JdtClass";
            display = new string(display.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
            if (display.Length > 72) display = display.Substring(0, 72);
            string hash;
            using (var sha = SHA256.Create())
                hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(address)))
                    .Replace("-", "").Substring(0, 16);
            string file = Path.Combine(cacheRoot, display + "_" + hash + ".java");
            Directory.CreateDirectory(cacheRoot);
            if (!File.Exists(file))
            {
                File.WriteAllText(file, source, new UTF8Encoding(false));
                File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
            }
            log("Classe JDK aperta da cache locale in sola lettura; l'URI jdt:// originale non e' stato registrato nell'editor.");
            return file;
        }
        private static string ExtractSource(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.String) return token.Value<string>();
            if (token.Type == JTokenType.Object) return (string)token["text"] ?? (string)token["content"];
            return null;
        }
    }

    internal sealed class JavaSymbolDestination
    {
        internal string Name;
        internal string Uri;
        internal int Line;
        internal int Column;
        public string Title => Name + "  (" + (Line + 1) + ")";
    }

    internal static class JavaNavigationDialog
    {
        internal static JavaSymbolDestination Choose(List<JavaSymbolDestination> all)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var list = new ListBox { MinWidth = 560, Height = 310, DisplayMemberPath = "Title" };
            var filter = new TextBox { MinWidth = 560, Margin = new Thickness(0, 0, 0, 8) };
            list.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            list.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            filter.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            filter.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            Action refresh = () =>
            {
                string s = filter.Text ?? "";
                list.ItemsSource = all.Where(x => x.Name.IndexOf(s,
                    StringComparison.OrdinalIgnoreCase) >= 0).Take(400).ToList();
                list.SelectedIndex = list.Items.Count > 0 ? 0 : -1;
            };
            filter.TextChanged += (s, e) => refresh();
            refresh();
            var ok = new Button { Content = "Apri", MinWidth = 90, IsDefault = true,
                Margin = new Thickness(5) };
            var cancel = new Button { Content = "Annulla", MinWidth = 90,
                IsCancel = true, Margin = new Thickness(5) };
            var row = new StackPanel { Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right };
            row.Children.Add(ok); row.Children.Add(cancel);
            var layout = new StackPanel { Margin = new Thickness(16) };
            layout.Children.Add(filter); layout.Children.Add(list); layout.Children.Add(row);
            var dialog = new DialogWindow { Title = "Navigazione simboli Java", Content = layout,
                Width = 640, Height = 460, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false };
            dialog.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.DialogBrushKey);
            dialog.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.DialogTextBrushKey);
            ok.Click += (s, e) => { dialog.DialogResult = true; dialog.Close(); };
            list.MouseDoubleClick += (s, e) => { if (list.SelectedItem != null)
                { dialog.DialogResult = true; dialog.Close(); } };
            dialog.Loaded += (s, e) => filter.Focus();
            return dialog.ShowModal() == true ? list.SelectedItem as JavaSymbolDestination : null;
        }
    }
}
