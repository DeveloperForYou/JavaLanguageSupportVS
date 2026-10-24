using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.FindAllReferences;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableControl;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.TextManager.Interop;
using Newtonsoft.Json.Linq;

namespace JavaLanguageSupport.Editor.Navigation
{
    /// <summary>
    /// Java Find All References in the NATIVE Visual Studio Find All References tool window.
    /// Locations come exclusively from JDT LS (textDocument/references); no textual
    /// matching. All existing entry points (Shift+F12, CodeLens, editor operations)
    /// still call RunAsync. No custom WPF window is created.
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

        internal static async Task RunAsync(string uri, ITextSnapshot snapshot, int caret,
            Action<string> log, CancellationToken token)
        {
            if (snapshot == null || string.IsNullOrEmpty(uri)) return;
            var args = new
            {
                textDocument = new { uri },
                position = JavaLspRpc.Position(snapshot, caret),
                context = new { includeDeclaration = true }
            };
            JToken reply = await JavaLspRpc.RequestAsync("textDocument/references", args,
                token).ConfigureAwait(false);

            // JDT LS can temporarily return [] while importing dependencies.
            // Wait only for a bounded interval, never reindex on each request.
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
                log("Java Find References: nessuna risposta LSP valida. " +
                    JavaWorkspaceActivity.Summary);
                return;
            }

            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var found = new List<Result>();
            // References inside JDK/dependency source can use jdt:// rather than
            // file://. Resolve each distinct external document just once and cap
            // requests: a large Find References result must remain responsive.
            var externalPaths = new Dictionary<string, string>(StringComparer.Ordinal);
            const int maxExternalSources = 16;
            int skippedExternal = 0;
            foreach (JToken item in ((JArray)reply).Take(10000))
            {
                string address = (string)item["uri"] ?? (string)item["targetUri"];
                JToken range = item["range"] ?? item["targetSelectionRange"] ??
                    item["targetRange"];
                Uri target;
                if (!Uri.TryCreate(address, UriKind.Absolute, out target) ||
                    range == null || range["start"] == null) continue;
                int line = (int?)range["start"]["line"] ?? -1;
                int col = (int?)range["start"]["character"] ?? -1;
                if (line < 0 || col < 0) continue;
                string path;
                if (target.IsFile)
                    path = target.LocalPath;
                else if (string.Equals(target.Scheme, "jdt", StringComparison.OrdinalIgnoreCase))
                {
                    if (!externalPaths.TryGetValue(address, out path))
                    {
                        if (externalPaths.Count >= maxExternalSources)
                        {
                            skippedExternal++;
                            continue;
                        }
                        path = await JavaNavigation.ResolvePathAsync(address, log, token)
                            .ConfigureAwait(false);
                        externalPaths.Add(address, path ?? "");
                    }
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        skippedExternal++;
                        continue;
                    }
                }
                else continue;
                path = Path.GetFullPath(path);
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

            // LSP coordinates refer to in-memory text, not necessarily the file on disk.
            // Do not expose offsets obtained from an outdated source snapshot.
            if (!ReferenceEquals(snapshot, snapshot.TextBuffer.CurrentSnapshot))
            {
                log("Java Find References: il documento sorgente e' cambiato. " +
                    "Ripetere la ricerca.");
                return;
            }
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            var openLines = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            Uri sourceUri;
            if (Uri.TryCreate(uri, UriKind.Absolute, out sourceUri) && sourceUri.IsFile &&
                snapshot.Length <= 4 * 1024 * 1024)
                openLines[Path.GetFullPath(sourceUri.LocalPath)] = SplitLines(snapshot.GetText());
            int probed = 0;
            // Beyond the RDT probe budget we cannot tell whether a document has
            // unsaved edits. Never display a potentially stale on-disk excerpt.
            var unverifiedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in found.Select(x => x.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (openLines.ContainsKey(path)) continue;
                // Avoid thousands of expensive RDT/COM probes on the editor thread.
                if (++probed > 64)
                {
                    unverifiedPaths.Add(path);
                    continue;
                }
                string[] lines = TryReadOpenBufferLines(path);
                if (lines != null) openLines[path] = lines;
            }

            await Task.Run(() =>
            {
                foreach (var group in found.GroupBy(x => x.Path,
                    StringComparer.OrdinalIgnoreCase))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        string[] lines;
                        if (!openLines.TryGetValue(group.Key, out lines))
                        {
                            if (unverifiedPaths.Contains(group.Key))
                            {
                                foreach (Result r in group)
                                    r.Excerpt = "(anteprima non verificata: aprire il file)";
                                continue;
                            }
                            if (new FileInfo(group.Key).Length > 4 * 1024 * 1024)
                            {
                                foreach (Result r in group)
                                    r.Excerpt = "(file grande: aprire per leggere)";
                                continue;
                            }
                            lines = File.ReadAllLines(group.Key);
                        }
                        foreach (Result r in group)
                            r.Excerpt = r.Line >= 0 && r.Line < lines.Length ?
                                Truncate(lines[r.Line].Trim(), 200) :
                                "(riga non presente nello snapshot)";
                    }
                    catch (IOException)
                    {
                        foreach (Result r in group) r.Excerpt = "(non leggibile)";
                    }
                    catch (UnauthorizedAccessException)
                    {
                        foreach (Result r in group) r.Excerpt = "(accesso negato)";
                    }
                }
            }, token).ConfigureAwait(false);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            if (!ReferenceEquals(snapshot, snapshot.TextBuffer.CurrentSnapshot))
            {
                log("Java Find References: il documento e' cambiato durante il caricamento " +
                    "delle anteprime. Ripetere la ricerca.");
                return;
            }
            ShowNativeWindow(found, log, ((JArray)reply).Count > 10000,
                skippedExternal > 0);
            if (skippedExternal > 0)
                log("Java Find References: " + skippedExternal +
                    " posizioni jdt:// non risolte o oltre il limite di " +
                    maxExternalSources + " sorgenti esterni; risultati parziali.");
        }

        private static void ShowNativeWindow(IList<Result> results, Action<string> log,
            bool truncated, bool partial)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var service = Package.GetGlobalService(typeof(SVsFindAllReferences))
                as IFindAllReferencesService;
            if (service == null)
            {
                // Never fall back to the removed custom WPF window.
                log("Java Find References: il servizio nativo SVsFindAllReferences " +
                    "non e' disponibile.");
                return;
            }
            string label = "Java — " + results.Count + " riferimenti" +
                (partial || truncated ? " (parziali)" : "");
            var window = service.StartSearch(label);
            if (window == null)
            {
                log("Java Find References: il servizio nativo non ha aperto la finestra.");
                return;
            }
            var source = new JavaReferenceTableSource(results.Select(r =>
                Tuple.Create(r.Path, r.Line, r.Column, r.Excerpt)));
            // The native window supplies a built-in Code/Text column from the
            // entry's StandardTableKeyNames.Text. Declaring Text here would add
            // a second, redundant Description column in some Visual Studio builds.
            window.Manager.AddSource(source,
                StandardTableKeyNames.DocumentName,
                StandardTableKeyNames.Line,
                StandardTableKeyNames.Column);
            // Only affects this native results window, not global VS preferences.
            ConfigureNoGrouping(window, log);
            window.SetProgress(1.0);
            log("Java Find References (nativo): " + results.Count + " posizioni " +
                "(inclusa la dichiarazione)." +
                (truncated ? " Risultati limitati a 10.000 posizioni." : "") +
                (results.Count == 0 && JavaWorkspaceActivity.IsBusy ?
                    " Indicizzazione in corso: riprovare al termine." : ""));
        }

        private static void ConfigureNoGrouping(IFindAllReferencesWindow window,
            Action<string> log)
        {
            // GroupingPriority -1 = ungrouped in the VS table control. Preserve
            // the columns' visibility, widths and sorting. This setting applies
            // to this search only; a user can change it from the native toolbar.
            try
            {
                var control = window.TableControl as IWpfTableControl2;
                if (control == null || control.ColumnStates == null) return;
                var states = control.ColumnStates.Select(s =>
                    (ColumnState)new ColumnState2(s.Name, s.IsVisible, s.Width,
                        s.SortPriority, s.DescendingSort, -1)).ToArray();
                if (states.Length != 0) control.SetColumnStates(states);
            }
            catch (Exception ex)
            {
                // Not all VS SDK builds expose the same grouping behavior.
                // The references must remain visible even if this preference fails.
                if (log != null) log("Java FAR: impossibile impostare Nessun raggruppamento: " +
                    ex.Message);
            }
        }

        private static string[] SplitLines(string text)
        {
            return text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        }

        // Read the existing VS buffer via the Running Document Table; don't open
        // tabs or save user edits. Called only on the UI thread (VS COM).
        // Also used by WPF Peek fallback, to show unsaved target documents.
        internal static string[] TryReadOpenBufferLines(string path)
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
    }

    // ITableDataSource is the supported integration point for Visual Studio's
    // native Find All References table, including its navigation and grouping.
    internal sealed class JavaReferenceTableSource : ITableDataSource
    {
        private readonly IList<JavaReferenceTableEntry> entries;

        internal JavaReferenceTableSource(IEnumerable<Tuple<string, int, int, string>> refs)
        {
            entries = refs.Select(x => new JavaReferenceTableEntry(x.Item1, x.Item2,
                x.Item3, x.Item4)).ToList();
        }

        public string SourceTypeIdentifier { get { return "java/jdtls/references"; } }
        public string Identifier { get { return "JavaLanguageSupport.ReferenceSearch"; } }
        public string DisplayName { get { return "Java References (JDT LS)"; } }

        public IDisposable Subscribe(ITableDataSink sink)
        {
            sink.AddEntries(entries.Cast<ITableEntry>().ToList(), true);
            return new JavaReferenceSinkRegistration(sink);
        }

        private sealed class JavaReferenceSinkRegistration : IDisposable
        {
            private readonly ITableDataSink sink;
            internal JavaReferenceSinkRegistration(ITableDataSink sink) { this.sink = sink; }
            public void Dispose() { sink.RemoveAllEntries(); }
        }
    }

    internal sealed class JavaReferenceTableEntry : ITableEntry
    {
        private readonly Dictionary<string, object> values = new Dictionary<string, object>();

        internal JavaReferenceTableEntry(string path, int line, int column, string excerpt)
        {
            values[StandardTableKeyNames.DocumentName] = path;
            values[StandardTableKeyNames.Line] = line;      // Visual Studio table uses 0-based
            values[StandardTableKeyNames.Column] = column; // LSP offsets are 0-based
            values[StandardTableKeyNames.Text] = excerpt ?? "";
        }

        public object Identity { get { return values; } }
        public bool CanSetValue(string keyName) { return true; }
        public bool TryGetValue(string keyName, out object content)
        {
            return values.TryGetValue(keyName, out content);
        }
        public bool TrySetValue(string keyName, object content)
        {
            values[keyName] = content;
            return true;
        }
    }
}
