using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Newtonsoft.Json.Linq;
using EnvDTE;
using WpfWindow = System.Windows.Window;

namespace JavaLanguageSupport.Editor.Navigation
{
    /// <summary>
    /// A modeless, navigable Find All References window. Source of truth: JDT LS
    /// textDocument/references (including declarations), not a text search.
    /// </summary>
    internal static class JavaReferenceSearch
    {
        private sealed class Result
        {
            internal string Path;
            internal int Line;
            internal int Column;
            internal string Excerpt;
        }

        private static WpfWindow currentWindow;

        internal static async Task RunAsync(string uri, ITextSnapshot snapshot, int caret,
            Action<string> log, CancellationToken token)
        {
            var args = new
            {
                textDocument = new { uri },
                position = JavaLspRpc.Position(snapshot, caret),
                context = new { includeDeclaration = true }
            };
            JToken reply = await JavaLspRpc.RequestAsync("textDocument/references", args,
                token).ConfigureAwait(false);
            // A cold workspace can report an empty list before Java types are
            // indexed. Bounded retries; never rescan source files textually.
            if ((reply is JArray && ((JArray)reply).Count == 0) ||
                (reply == null && JavaWorkspaceActivity.IsBusy))
            {
                await JavaWorkspaceActivity.WaitIfBusyAsync(2500, token)
                    .ConfigureAwait(false);
                int[] waits = JavaWorkspaceActivity.IsBusy ?
                    new[] { 350, 900, 1700 } : new[] { 350, 900 };
                foreach (int delay in waits)
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                    reply = await JavaLspRpc.RequestAsync("textDocument/references", args,
                        token).ConfigureAwait(false);
                    if (reply is JArray && ((JArray)reply).Count != 0) break;
                }
            }
            token.ThrowIfCancellationRequested();
            if (!(reply is JArray))
            {
                log("Find References: nessun elenco. " + JavaWorkspaceActivity.Summary);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                MessageBox.Show("Nessun riferimento disponibile. Controlla l'indicizzazione JDT LS.",
                    "Java: Trova tutti i riferimenti", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var found = new List<Result>();
            foreach (JToken item in ((JArray)reply).Take(10000))
            {
                string address = (string)item["uri"] ?? (string)item["targetUri"];
                JToken range = item["range"] ?? item["targetSelectionRange"] ?? item["targetRange"];
                Uri target;
                if (!Uri.TryCreate(address, UriKind.Absolute, out target) || !target.IsFile ||
                    range == null || range["start"] == null) continue;
                int line = (int?)range["start"]["line"] ?? -1;
                int col = (int?)range["start"]["character"] ?? -1;
                if (line < 0 || col < 0) continue;
                string path = Path.GetFullPath(target.LocalPath);
                if (!unique.Add(path + "|" + line + "|" + col)) continue;
                found.Add(new Result { Path = path, Line = line, Column = col });
            }
            found.Sort((a, b) =>
            {
                int n = StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path);
                if (n != 0) return n;
                n = a.Line.CompareTo(b.Line);
                return n == 0 ? a.Column.CompareTo(b.Column) : n;
            });

            // Positions returned by JDT LS apply to the in-memory buffers, not
            // necessarily to File.ReadAllLines on disk. Capture live snapshots of
            // already-open documents without opening additional editor tabs.
            // If the search started before an intervening edit, never show stale
            // line numbers pretending to be current references.
            if (!ReferenceEquals(snapshot, snapshot.TextBuffer.CurrentSnapshot))
            {
                log("Trova riferimenti: il documento e' cambiato durante la ricerca. Riprova.");
                return;
            }
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            var openLines = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            Uri sourceUri;
            if (Uri.TryCreate(uri, UriKind.Absolute, out sourceUri) && sourceUri.IsFile &&
                snapshot.Length <= 4 * 1024 * 1024)
                openLines[Path.GetFullPath(sourceUri.LocalPath)] = SplitLines(snapshot.GetText());
            int probed = 0;
            foreach (var path in found.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (openLines.ContainsKey(path)) continue;
                // Limit Running Document Table probes on the VS UI thread.
                if (++probed > 64) break;
                string[] lines = TryReadOpenBufferLines(path);
                if (lines != null) openLines[path] = lines;
            }
            // Only unopened files require disk I/O; keep it off the UI thread.
            await Task.Run(() =>
            {
                foreach (var group in found.GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        string[] lines;
                        if (!openLines.TryGetValue(group.Key, out lines))
                        {
                            if (new FileInfo(group.Key).Length > 4 * 1024 * 1024)
                            {
                                foreach (var match in group)
                                    match.Excerpt = "(file grande: apri per leggere)";
                                continue;
                            }
                            lines = File.ReadAllLines(group.Key);
                        }
                        foreach (var match in group)
                            match.Excerpt = match.Line >= 0 && match.Line < lines.Length ?
                                Truncate(lines[match.Line].Trim(), 200) :
                                "(riga non presente nello snapshot)";
                    }
                    catch (IOException)
                    {
                        foreach (var match in group) match.Excerpt = "(non leggibile)";
                    }
                    catch (UnauthorizedAccessException)
                    {
                        foreach (var match in group) match.Excerpt = "(accesso negato)";
                    }
                }
            }, token).ConfigureAwait(false);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            Action refreshWhenIdle = null;
            // Requery once when LSP progress settles, but only if the editor
            // snapshot is unchanged. Avoid creating a stale reference list.
            if (found.Count == 0 && JavaWorkspaceActivity.IsBusy)
                refreshWhenIdle = () =>
                {
                    if (snapshot.TextBuffer.CurrentSnapshot.Version.VersionNumber !=
                        snapshot.Version.VersionNumber) return;
                    ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
                    {
                        await Task.Delay(350, token).ConfigureAwait(false);
                        await RunAsync(uri, snapshot, caret, log, token).ConfigureAwait(false);
                    }).FileAndForget("JavaLanguageSupport/ReferenceRefresh");
                };
            ShowWindow(found, log, ((JArray)reply).Count > 10000, refreshWhenIdle);
        }

        private static string[] SplitLines(string text)
        {
            return text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        }

        // Best-effort: read an existing VS editor buffer from the RDT. Do not
        // open another file, save unsaved changes or block for JDT LS here.
        // Must run on the UI thread (VS COM services are apartment-affine).
        private static string[] TryReadOpenBufferLines(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var running = Package.GetGlobalService(typeof(SVsRunningDocumentTable))
                as IVsRunningDocumentTable;
            var component = Package.GetGlobalService(typeof(SComponentModel))
                as IComponentModel;
            var adapters = component == null ? null :
                component.GetService<IVsEditorAdaptersFactoryService>();
            if (running == null || adapters == null) return null;
            IVsHierarchy hierarchy;
            uint itemId, cookie;
            IntPtr documentData;
            int hr = running.FindAndLockDocument((uint)_VSRDTFLAGS.RDT_NoLock,
                path, out hierarchy, out itemId, out documentData, out cookie);
            if (hr < 0 || documentData == IntPtr.Zero) return null;
            try
            {
                var native = Marshal.GetObjectForIUnknown(documentData) as IVsTextBuffer;
                if (native == null) return null;
                var buffer = adapters.GetDocumentBuffer(native) ?? adapters.GetDataBuffer(native);
                if (buffer == null || buffer.CurrentSnapshot.Length > 4 * 1024 * 1024)
                    return null;
                return SplitLines(buffer.CurrentSnapshot.GetText());
            }
            catch (COMException) { return null; }
            catch (InvalidCastException) { return null; }
            finally { Marshal.Release(documentData); }
        }

        private static string Truncate(string text, int max)
        {
            return text.Length <= max ? text : text.Substring(0, max - 1) + "…";
        }

        private static void ShowWindow(IList<Result> results, Action<string> log,
            bool truncated, Action refreshWhenIdle)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var tree = new TreeView { Margin = new Thickness(10, 4, 10, 8) };
            JavaNavigationWindowStyle.SetupTree(tree);

            foreach (var group in results.GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
            {
                var header = JavaNavigationWindowStyle.TreeHeader(
                    Path.GetFileName(group.Key) + " — " + group.Count() +
                    " riferimenti  (" + group.Key + ")", true);
                var fileNode = new TreeViewItem { Header = header, IsExpanded = true };
                foreach (Result r in group)
                {
                    Result clicked = r;
                    var label = JavaNavigationWindowStyle.TreeHeader(
                        "Riga " + (r.Line + 1) + ", col " + (r.Column + 1) +
                        "   " + r.Excerpt, false);
                    var node = new TreeViewItem { Header = label, Tag = r };
                    node.MouseDoubleClick += (sender, args) =>
                    {
                        args.Handled = true;
                        Navigate(clicked, log);
                    };
                    fileNode.Items.Add(node);
                }
                tree.Items.Add(fileNode);
            }

            var title = new TextBlock { Text = "Java: Trova tutti i riferimenti — " +
                results.Count + " occorrenze in " +
                results.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() + " file",
                FontSize = 14, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(12, 10, 12, 5) };
            JavaNavigationWindowStyle.ThemeText(title);
            var note = new TextBlock { Text =
                (results.Count == 0 ? "Nessun risultato dopo l'attesa dell'indice Java. Riprova a importazione completata. " : "") +
                (truncated ? "Mostrati solo i primi 10.000 risultati. " : "") +
                "Doppio clic su un risultato per aprire il sorgente. " +
                "Le anteprime usano il buffer aperto quando disponibile (altrimenti il disco). " +
                "Il totale include la dichiarazione; CodeLens conta solo gli utilizzi.",
                Margin = new Thickness(12, 0, 12, 5), TextWrapping = TextWrapping.Wrap };
            JavaNavigationWindowStyle.ThemeText(note);

            var panel = new DockPanel();
            DockPanel.SetDock(title, Dock.Top);
            DockPanel.SetDock(note, Dock.Top);
            panel.Children.Add(title);
            panel.Children.Add(note);
            panel.Children.Add(tree);
            var window = new WpfWindow {
                Title = "Java — Trova tutti i riferimenti", Width = 880, Height = 580,
                MinWidth = 600, MinHeight = 300, Content = panel,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.CanResize, ShowInTaskbar = false
            };
            // Consistent dark/light theme and no minimize button.
            JavaNavigationWindowStyle.SetupWindow(window);
            if (currentWindow != null) currentWindow.Close();
            currentWindow = window;
            // Subscribe only while there is a pending empty result.
            EventHandler onIdle = null;
            if (refreshWhenIdle != null)
            {
                onIdle = (sender, args) =>
                {
                    ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        if (!ReferenceEquals(currentWindow, window) || !window.IsVisible) return;
                        JavaWorkspaceActivity.BecameIdle -= onIdle;
                        onIdle = null;
                        refreshWhenIdle();
                    }).FileAndForget("JavaLanguageSupport/ReferenceIdle");
                };
                JavaWorkspaceActivity.BecameIdle += onIdle;
            }
            window.Closed += (sender, args) =>
            {
                if (onIdle != null) JavaWorkspaceActivity.BecameIdle -= onIdle;
                if (ReferenceEquals(currentWindow, window)) currentWindow = null;
            };
            tree.KeyDown += (sender, args) =>
            {
                if (args.Key != Key.Enter) return;
                var item = tree.SelectedItem as TreeViewItem;
                var result = item == null ? null : item.Tag as Result;
                if (result == null) return;
                args.Handled = true;
                Navigate(result, log);
            };
            window.Show();
            window.Activate();
            log("Find References: " + results.Count + " occorrenze.");
        }

        private static void Navigate(Result r, Action<string> log)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var dte = Package.GetGlobalService(typeof(SDTE)) as DTE;
                if (dte == null) throw new InvalidOperationException("DTE non disponibile.");
                dte.ItemOperations.OpenFile(r.Path, EnvDTE.Constants.vsViewKindTextView);
                var selection = dte.ActiveDocument == null ? null :
                    dte.ActiveDocument.Selection as TextSelection;
                if (selection != null)
                    selection.MoveToLineAndOffset(r.Line + 1, r.Column + 1, false);
            }
            catch (Exception ex) { log("Navigazione riferimento: " + ex.Message); }
        }
    }
}
