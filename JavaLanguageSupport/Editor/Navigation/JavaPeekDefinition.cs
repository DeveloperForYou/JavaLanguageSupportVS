using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Newtonsoft.Json.Linq;

namespace JavaLanguageSupport.Editor.Navigation
{
    /// <summary>
    /// Read-only definition preview, without switching the active Java file.
    /// This is a themed WPF dialog, NOT the native Visual Studio inline Peek host.
    /// </summary>
    internal static class JavaPeekDefinition
    {
        internal static async Task RunAsync(string sourceUri, ITextSnapshot sourceSnapshot,
            int caret, Action<string> log, CancellationToken token)
        {
            object args = JavaAdvancedNavigation.PositionArguments(sourceUri, sourceSnapshot, caret);
            JToken reply = await JavaLspRpc.RequestAsync("textDocument/definition", args, token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(sourceSnapshot, sourceSnapshot.TextBuffer.CurrentSnapshot))
            {
                log("Java Peek: documento modificato durante la ricerca. Ripeti ALT+F12.");
                return;
            }
            var locations = JavaAdvancedNavigation.Objects(reply)
                .Select(JavaAdvancedNavigation.Location).Where(x => x != null)
                .GroupBy(x => x.Uri + ":" + x.Line + ":" + x.Column, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First()).ToList();
            if (locations.Count == 0)
            {
                log("Peek Definition: nessuna definizione. " + JavaWorkspaceActivity.Summary);
                return;
            }
            JavaAdvancedLocation target = locations[0];
            if (locations.Count > 1)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                var choices = locations.Select(x => new JavaSymbolDestination {
                    Name = System.IO.Path.GetFileNameWithoutExtension(new Uri(x.Uri).AbsolutePath),
                    Uri = x.Uri, Line = x.Line, Column = x.Column
                }).ToList();
                var choice = JavaNavigationDialog.Choose(choices);
                if (choice == null) return;
                target = locations.First(x => x.Uri == choice.Uri &&
                    x.Line == choice.Line && x.Column == choice.Column);
            }

            string path = await JavaNavigation.ResolvePathAsync(target.Uri, log, token);
            if (string.IsNullOrEmpty(path)) return;
            token.ThrowIfCancellationRequested();
            // Read unsaved target buffers from VS when available; disk access is
            // moved off the UI thread. This is the *fallback* WPF Peek only;
            // native ALT+F12 continues to use the Visual Studio Peek editor.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            string[] lines;
            if (JavaLspRpc.SameUri(sourceUri, target.Uri))
                lines = sourceSnapshot.GetText().Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            else
            {
                lines = JavaReferenceSearch.TryReadOpenBufferLines(path);
                if (lines == null)
                    lines = await Task.Run(() => File.ReadAllLines(path), token)
                        .ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(sourceSnapshot, sourceSnapshot.TextBuffer.CurrentSnapshot))
            {
                log("Java Peek: documento modificato prima dell'anteprima. Ripeti ALT+F12.");
                return;
            }
            if (lines.Length == 0) lines = new[] { "" };
            int first = Math.Max(0, target.Line - 8);
            int last = Math.Min(lines.Length - 1, target.Line + 32);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            if (!ReferenceEquals(sourceSnapshot, sourceSnapshot.TextBuffer.CurrentSnapshot)) return;
            Show(target, path, lines, first, last, log);
        }

        private static void Show(JavaAdvancedLocation location, string path,
            string[] lines, int first, int last, Action<string> log)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dialog = new System.Windows.Window {
                Title = "Java: Anteprima definizione — " + Path.GetFileName(path),
                Width = 940, Height = 615, MinWidth = 580, MinHeight = 360,
                ResizeMode = ResizeMode.CanResizeWithGrip,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false
            };
            JavaNavigationWindowStyle.SetupWindow(dialog);
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var header = new TextBlock {
                Text = path + "  (riga " + (location.Line + 1) + ")",
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 9),
                FontWeight = FontWeights.SemiBold
            };
            JavaNavigationWindowStyle.ThemeText(header);
            Grid.SetRow(header, 0);
            root.Children.Add(header);
            // A RichTextBox with a FlowDocument allows colorized, selectable Java
            // while keeping the existing modal preview and navigation actions.
            // The preview has lexical highlighting; it is not a second LSP editor.
            var source = new RichTextBox {
                Document = JavaPreviewSyntax.Create(lines, first, last, location.Line),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                IsReadOnly = true,
                IsReadOnlyCaretVisible = false,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(1)
            };
            JavaNavigationWindowStyle.ThemeControl(source);
            // Refresh the lexical fallback when the dialog becomes active.
            // This re-reads the current VS palette and never mutates the editor.
            dialog.Activated += (sender, args) =>
            {
                if (dialog.IsVisible)
                    source.Document = JavaPreviewSyntax.Create(lines, first, last,
                        location.Line);
            };
            Grid.SetRow(source, 1);
            root.Children.Add(source);
            var row = new StackPanel {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            var open = new Button { Content = "Apri definizione", MinWidth = 130,
                Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 5, 12, 5) };
            var close = new Button { Content = "Chiudi", MinWidth = 95,
                IsCancel = true, Padding = new Thickness(12, 5, 12, 5) };
            open.Click += async (sender, e) => {
                try
                {
                    dialog.Close();
                    await JavaAdvancedNavigation.OpenAsync(location, log, CancellationToken.None);
                }
                catch (Exception ex) { log("Peek Definition: " + ex.Message); }
            };
            close.Click += (sender, e) => dialog.Close();
            row.Children.Add(open);
            row.Children.Add(close);
            Grid.SetRow(row, 2);
            root.Children.Add(row);
            dialog.Content = root;
            dialog.ShowDialog();
        }
    }
}
