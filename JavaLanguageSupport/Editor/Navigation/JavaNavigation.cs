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
                case JavaLanguageSupport.Editor.Refactoring.JavaEditorOperation.GoToImplementation:
                case JavaLanguageSupport.Editor.Refactoring.JavaEditorOperation.GoToTypeDefinition:
                    string method = operation == JavaLanguageSupport.Editor.Refactoring.JavaEditorOperation.GoToImplementation
                        ? "textDocument/implementation" : "textDocument/typeDefinition";
                    result = await JavaLspRpc.RequestAsync(method,
                        new { textDocument = new { uri = documentUri }, position = JavaLspRpc.Position(snapshot, caret) },
                        token).ConfigureAwait(false);
                    if (operation == JavaLanguageSupport.Editor.Refactoring.JavaEditorOperation.GoToImplementation &&
                        (result == null || result.Type == JTokenType.Null ||
                         (result is JArray && !((JArray)result).Any()))) {
                        // Force symbol resolution and allow JDT LS's index to settle.
                        // This call has no side effects on user files.
                        await JavaLspRpc.RequestAsync("textDocument/definition",
                            new { textDocument = new { uri = documentUri },
                                position = JavaLspRpc.Position(snapshot, caret) },
                            token).ConfigureAwait(false);
                        // If JDT LS has reported active indexing, wait briefly
                        // off the UI thread instead of treating the first empty
                        // result as a definitive "no implementations".
                        await JavaWorkspaceActivity.WaitIfBusyAsync(2500, token)
                            .ConfigureAwait(false);
                        int[] waits = JavaWorkspaceActivity.ImplementationRetryDelays();
                        foreach (int wait in waits) {
                            token.ThrowIfCancellationRequested();
                            await Task.Delay(wait, token).ConfigureAwait(false);
                            result = await JavaLspRpc.RequestAsync("textDocument/implementation",
                                new { textDocument = new { uri = documentUri },
                                    position = JavaLspRpc.Position(snapshot, caret) },
                                token).ConfigureAwait(false);
                            if (result != null && result.Type != JTokenType.Null &&
                                (!(result is JArray) || ((JArray)result).Count > 0)) break;
                        }
                    }
                    ReadLocations(targets, result, "Destinazione");
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
            // LSP results describe the source snapshot at request time. Do not
            // navigate to an outdated symbol if the user edited the file while
            // JDT LS was resolving it (including the implementation retries).
            if (!ReferenceEquals(snapshot, snapshot.TextBuffer.CurrentSnapshot))
            {
                log("Java: il documento e' cambiato durante la navigazione. Ripeti il comando.");
                return;
            }
            // JDT LS can occasionally report the same implementation more than
            // once (Location and LocationLink, or duplicated workspace entries).
            // Collapse only identical locations, preserving distinct overloads.
            if (operation == JavaLanguageSupport.Editor.Refactoring.JavaEditorOperation.GoToDefinition ||
                operation == JavaLanguageSupport.Editor.Refactoring.JavaEditorOperation.GoToImplementation ||
                operation == JavaLanguageSupport.Editor.Refactoring.JavaEditorOperation.GoToTypeDefinition)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                targets.RemoveAll(item => !seen.Add(item.Uri + "|" + item.Line + "|" + item.Column));
            }
            if (targets.Count == 0)
            {
                log("JDT LS: nessuna destinazione. " + JavaWorkspaceActivity.Summary);
                return;
            }
            JavaSymbolDestination chosen = targets[0];
            // Native-like behavior: navigate immediately if there is exactly
            // one target. Explicit symbol browsers still keep their chooser.
            if (targets.Count > 1 ||
                operation == JavaLanguageSupport.Editor.Refactoring.JavaEditorOperation.GoToDocumentSymbol ||
                operation == JavaLanguageSupport.Editor.Refactoring.JavaEditorOperation.GoToWorkspaceSymbol)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                chosen = JavaNavigationDialog.Choose(targets);
            }
            if (chosen == null) return;
            string path = await ResolvePathAsync(chosen.Uri, log, token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(path)) return;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            // A jdt:// source resolution may take time too. Check again on the
            // UI thread immediately before changing the active VS document.
            if (!ReferenceEquals(snapshot, snapshot.TextBuffer.CurrentSnapshot))
            {
                log("Java: il documento e' cambiato prima dell'apertura. Ripeti il comando.");
                return;
            }
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
            // Location/LocationLink have no symbol name. Use the Java source
            // filename as the class/implementation name (e.g. UpperFormatter).
            // This also works when JDT LS returns multiple implementations.
            string display = label;
            Uri parsed;
            if (Uri.TryCreate(uri, UriKind.Absolute, out parsed))
            {
                string basename = Path.GetFileNameWithoutExtension(
                    parsed.IsFile ? parsed.LocalPath : parsed.AbsolutePath);
                if (!string.IsNullOrWhiteSpace(basename)) display = basename;
            }
            Add(destinations, display, uri, range);
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

        internal static async Task<string> ResolvePathAsync(string address, Action<string> log,
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
            // Include the retrieved source in the cache identity. JDK source
            // attachments and class files can change after JDK/dependency updates;
            // an URI-only key would otherwise reopen outdated read-only text.
            // Existing cached .java files remain untouched (and read-only).
            using (var sha = SHA256.Create())
                hash = BitConverter.ToString(sha.ComputeHash(
                    Encoding.UTF8.GetBytes(address + "\n" + source)))
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
        public string Title
        {
            get
            {
                string file = "";
                Uri location;
                if (System.Uri.TryCreate(this.Uri, UriKind.Absolute, out location))
                    file = Path.GetFileName(location.IsFile ? location.LocalPath : location.AbsolutePath);
                return Name + "    —    " + (string.IsNullOrEmpty(file) ? "Java" : file) +
                    ":" + (Line + 1);
            }
        }
    }

    internal static class JavaNavigationDialog
    {
        internal static JavaSymbolDestination Choose(List<JavaSymbolDestination> all)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var description = new TextBlock {
                Text = "Seleziona una destinazione (" + all.Count + "):",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 5)
            };
            JavaNavigationWindowStyle.ThemeText(description);

            var filter = new TextBox { Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(5, 3, 5, 3) };
            JavaNavigationWindowStyle.ThemeControl(filter);
            var list = new ListBox { MinHeight = 120 };
            JavaNavigationWindowStyle.SetupList(list);

            Action refresh = () =>
            {
                string query = filter.Text ?? "";
                list.Items.Clear();
                foreach (JavaSymbolDestination destination in all.Where(x =>
                    x.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).Take(400))
                {
                    var row = new ListBoxItem { Tag = destination, Padding = new Thickness(6, 4, 6, 4) };
                    row.Content = JavaNavigationWindowStyle.ListHeader(destination.Title);
                    list.Items.Add(row);
                }
                list.SelectedIndex = list.Items.Count > 0 ? 0 : -1;
            };
            filter.TextChanged += (sender, e) => refresh();
            refresh();

            var ok = new Button { Content = "Apri", MinWidth = 90, IsDefault = true,
                Margin = new Thickness(5, 0, 0, 0), Padding = new Thickness(8, 4, 8, 4) };
            var cancel = new Button { Content = "Annulla", MinWidth = 90,
                IsCancel = true, Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(8, 4, 8, 4) };
            JavaNavigationWindowStyle.ThemeControl(ok);
            JavaNavigationWindowStyle.ThemeControl(cancel);
            var actions = new StackPanel { Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right };
            actions.Children.Add(ok);
            actions.Children.Add(cancel);

            var grid = new Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(description);
            Grid.SetRow(filter, 1); grid.Children.Add(filter);
            Grid.SetRow(list, 2); grid.Children.Add(list);
            actions.Margin = new Thickness(0, 10, 0, 0);
            Grid.SetRow(actions, 3); grid.Children.Add(actions);

            var dialog = new System.Windows.Window {
                Title = "Navigazione simboli Java", Content = grid,
                Width = 810, Height = 520, MinWidth = 570, MinHeight = 330,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.CanResize, ShowInTaskbar = false
            };
            JavaNavigationWindowStyle.SetupWindow(dialog);
            Func<JavaSymbolDestination> selected = () =>
            {
                var row = list.SelectedItem as ListBoxItem;
                return row == null ? null : row.Tag as JavaSymbolDestination;
            };
            ok.Click += (sender, e) =>
            {
                if (selected() != null) dialog.DialogResult = true;
            };
            cancel.Click += (sender, e) => dialog.DialogResult = false;
            list.MouseDoubleClick += (sender, e) =>
            {
                if (selected() != null) dialog.DialogResult = true;
            };
            dialog.Loaded += (sender, e) => filter.Focus();
            return dialog.ShowDialog() == true ? selected() : null;
        }
    }
}
