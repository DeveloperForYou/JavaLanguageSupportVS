// U4.B - Safe session-only detached replay for Java symbol rename.
// This code does NOT use closed ITextBuffer/ITextDocument instances to replay.
// The existing linked native transaction remains the fast path while all
// participating documents are alive; this fallback starts only after closing.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using EnvDTE;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Threading;

namespace JavaLanguageSupport.Editor.Refactoring
{
    internal static partial class JavaProjectRenameEdits
    {
        private sealed class SymbolRenameDetachedState
        {
            private const long MaxSnapshotBytes = 16L * 1024 * 1024;
            private const int MaxFiles = 256;

            private sealed class SavedFile
            {
                internal string BeforePath;
                internal string AfterPath;
                internal byte[] Before;
                internal byte[] After;
            }

            private sealed class OpenTab
            {
                internal Document Document;
                internal string CurrentPath;
                internal string ResultPath;
            }

            private readonly RenamePlan plan;
            private readonly List<SavedFile> files;
            private RenameFileUndoPrimitive primitive;
            private bool ready;
            private bool disabled;

            private SymbolRenameDetachedState(RenamePlan source, List<SavedFile> snapshots)
            {
                plan = source;
                files = snapshots;
            }

            // Best effort: failure to capture snapshots must not break the
            // already functioning U4.A rename or its native Undo history.
            internal static SymbolRenameDetachedState TryCaptureBefore(RenamePlan plan)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                try
                {
                    if (plan == null || plan.targets.Count == 0 ||
                        plan.targets.Count > MaxFiles || plan.moves.Count > MaxFiles)
                        return null;
                    var snapshots = new List<SavedFile>();
                    var beforePaths = new HashSet<string>(PathCompare);
                    var afterPaths = new HashSet<string>(PathCompare);
                    long totalBytes = 0;
                    foreach (Target target in plan.targets)
                    {
                        if (target.Document == null || target.Buffer == null ||
                            target.Original == null || target.Document.IsDirty ||
                            target.Document.IsReloading ||
                            target.Buffer.CurrentSnapshot.Version.VersionNumber !=
                            target.Original.Version.VersionNumber ||
                            !PathCompare.Equals(target.Document.FilePath, target.Path) ||
                            !File.Exists(target.Path) ||
                            !InsideProject(plan.Root, target.Path) ||
                            !NoReparseFromRoot(plan.Root, target.Path) ||
                            !beforePaths.Add(target.Path))
                            return null;
                        var move = plan.moves.FirstOrDefault(m =>
                            PathCompare.Equals(m.From, target.Path));
                        string afterPath = move == null ? target.Path : move.To;
                        if (!InsideProject(plan.Root, afterPath) ||
                            !PathCompare.Equals(Path.GetDirectoryName(target.Path),
                                Path.GetDirectoryName(afterPath)) ||
                            !afterPaths.Add(afterPath))
                            return null;
                        byte[] before = File.ReadAllBytes(target.Path);
                        if (before.Length > MaxSnapshotBytes - totalBytes ||
                            !string.Equals(File.ReadAllText(target.Path), target.Before,
                                StringComparison.Ordinal))
                            return null;
                        totalBytes += before.Length;
                        snapshots.Add(new SavedFile {
                            BeforePath = target.Path, AfterPath = afterPath, Before = before
                        });
                    }
                    if (plan.moves.Any(m => !beforePaths.Contains(m.From) ||
                        PathCompare.Equals(m.From, m.To) ||
                        !PathCompare.Equals(Path.GetDirectoryName(m.From),
                            Path.GetDirectoryName(m.To))))
                        return null;
                    // Avoid in-place name swaps and any ambiguous move graph.
                    if (plan.moves.Any(m => beforePaths.Contains(m.To) || File.Exists(m.To)))
                        return null;
                    return new SymbolRenameDetachedState(plan, snapshots);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine("[Java Symbol U4.B] Snapshot prima non disponibile: " + ex);
                    return null;
                }
            }

            internal void Bind(RenameFileUndoPrimitive undoPrimitive)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                primitive = undoPrimitive;
            }

            // Called AFTER all target.Document.Save() operations finish.
            // The registration has already taken place in U4.A to preserve
            // the tested top native OLE marker; callbacks remain gated until ready.
            internal void CompleteAfterSave()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (disabled || primitive == null) return;
                try
                {
                    long totalBytes = files.Sum(f => (long)f.Before.Length);
                    foreach (SavedFile file in files)
                    {
                        var target = plan.targets.First(t =>
                            PathCompare.Equals(t.Path, file.BeforePath));
                        if (target.Document.IsDirty || target.Document.IsReloading ||
                            !PathCompare.Equals(target.Document.FilePath, file.AfterPath) ||
                            !File.Exists(file.AfterPath) ||
                            (!PathCompare.Equals(file.BeforePath, file.AfterPath) &&
                             File.Exists(file.BeforePath)))
                            throw new IOException("Documento non salvato/coerente: " + file.AfterPath);
                        byte[] after = File.ReadAllBytes(file.AfterPath);
                        if (after.Length > MaxSnapshotBytes - totalBytes ||
                            !string.Equals(File.ReadAllText(file.AfterPath),
                                target.Buffer.CurrentSnapshot.GetText(), StringComparison.Ordinal))
                            throw new IOException("Snapshot finale non coerente: " + file.AfterPath);
                        totalBytes += after.Length;
                        file.After = after;
                    }
                    ready = true;
                }
                catch (Exception ex)
                {
                    disabled = true;
                    ready = false;
                    foreach (SavedFile file in files) file.After = null;
                    Trace.WriteLine("[Java Symbol U4.B] Replay detached non disponibile: " + ex);
                }
            }

            private static bool InsideProject(string root, string path)
            {
                string project = Path.GetFullPath(root).TrimEnd('\\', '/');
                string full = Path.GetFullPath(path);
                return full.StartsWith(project + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
            }

            private static bool NoReparseFromRoot(string root, string path)
            {
                string project = Path.GetFullPath(root).TrimEnd('\\', '/');
                string full = Path.GetFullPath(path);
                if (!InsideProject(root, full)) return false;
                if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                    return false;
                for (string dir = Path.GetDirectoryName(full);
                    !string.IsNullOrEmpty(dir) &&
                    (PathCompare.Equals(dir, project) || InsideProject(root, dir));
                    dir = Path.GetDirectoryName(dir))
                {
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                        return false;
                    if (PathCompare.Equals(dir, project)) break;
                }
                return true;
            }

            // Called on every native QueryStatus. This is intentionally cheap;
            // full hashes, open editors and RDT checks belong in ReplayDetached.
            internal bool CanReplayDetached(bool redo)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (!ready || disabled || primitive == null || primitive.IsApplied == redo)
                    return false;
                // The toolbar polls this method frequently. Probe one canonical
                // path only; VerifyOnDisk rechecks EVERY file/byte at execution.
                SavedFile sample = files.FirstOrDefault(f =>
                    !PathCompare.Equals(f.BeforePath, f.AfterPath)) ?? files[0];
                string source = redo ? sample.BeforePath : sample.AfterPath;
                string destination = redo ? sample.AfterPath : sample.BeforePath;
                return File.Exists(source) &&
                    (PathCompare.Equals(source, destination) || !File.Exists(destination));
            }

            private void VerifyOnDisk(bool redo)
            {
                if (!CanReplayDetached(redo))
                    throw new IOException("U4.B: direzione o file non compatibili con Undo/Redo.");
                foreach (SavedFile file in files)
                {
                    string path = redo ? file.BeforePath : file.AfterPath;
                    string destination = redo ? file.AfterPath : file.BeforePath;
                    byte[] expected = redo ? file.Before : file.After;
                    if (!File.Exists(path) ||
                        (!PathCompare.Equals(path, destination) &&
                         (File.Exists(destination) || Directory.Exists(destination))) ||
                        expected == null ||
                        (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0 ||
                        !NoReparseFromRoot(plan.Root, path) ||
                        !File.ReadAllBytes(path).SequenceEqual(expected))
                        throw new IOException("U4.B: file cambiato dopo la rinomina: " + path);
                }
            }

            private List<OpenTab> PrepareTabs(DTE dte, bool redo)
            {
                var tabs = new List<OpenTab>();
                if (dte == null || dte.Documents == null || dte.ItemOperations == null)
                    throw new InvalidOperationException("U4.B: DTE/documenti non disponibili.");
                var sources = files.ToDictionary(
                    f => redo ? f.BeforePath : f.AfterPath, f => f, PathCompare);
                var model = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
                var adapters = model == null ? null :
                    model.GetService<IVsEditorAdaptersFactoryService>();
                var factory = model == null ? null :
                    model.GetService<ITextDocumentFactoryService>();
                if (adapters == null || factory == null)
                    throw new InvalidOperationException("U4.B: servizi buffer/RDT non disponibili.");
                foreach (Document document in dte.Documents)
                {
                    string path;
                    try { path = Path.GetFullPath(document.FullName); }
                    catch (Exception) { continue; }
                    SavedFile snapshot;
                    if (!sources.TryGetValue(path, out snapshot)) continue;
                    if (!document.Saved)
                        throw new IOException("U4.B: salva prima il documento: " + path);
                    ITextBuffer buffer = TryGetOpenDocumentBuffer(path, adapters);
                    ITextDocument textDocument;
                    if (buffer == null || !factory.TryGetTextDocument(buffer, out textDocument) ||
                        textDocument == null || textDocument.IsDirty || textDocument.IsReloading ||
                        !PathCompare.Equals(textDocument.FilePath, path) ||
                        !string.Equals(buffer.CurrentSnapshot.GetText(), File.ReadAllText(path),
                            StringComparison.Ordinal))
                        throw new IOException("U4.B: buffer aperto non coerente: " + path);
                    tabs.Add(new OpenTab {
                        Document = document,
                        CurrentPath = path,
                        ResultPath = redo ? snapshot.AfterPath : snapshot.BeforePath
                    });
                }
                return tabs;
            }

            private static void ReopenTabs(DTE dte, IEnumerable<OpenTab> tabs, bool result)
            {
                foreach (OpenTab tab in tabs)
                {
                    string path = result ? tab.ResultPath : tab.CurrentPath;
                    try
                    {
                        if (File.Exists(path))
                            dte.ItemOperations.OpenFile(path, EnvDTE.Constants.vsViewKindTextView);
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine("[Java Symbol U4.B] Scheda non riaperta: " + path + ": " + ex);
                    }
                }
            }

            internal void ReplayDetached(bool redo)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                VerifyOnDisk(redo);
                var dte = Package.GetGlobalService(typeof(SDTE)) as DTE;
                List<OpenTab> tabs = PrepareTabs(dte, redo);
                var closed = new List<OpenTab>();
                try
                {
                    foreach (OpenTab tab in tabs)
                    {
                        tab.Document.Close(vsSaveChanges.vsSaveChangesNo);
                        closed.Add(tab);
                    }
                }
                catch
                {
                    ReopenTabs(dte, closed, false);
                    throw;
                }
                var succeededMoves = new List<FileRename>();
                try
                {
                    // Revalidate after closing documents (VS may have changed disk state).
                    VerifyOnDisk(redo);
                    foreach (FileRename move in redo ? plan.moves : plan.moves.AsEnumerable().Reverse())
                    {
                        string from = redo ? move.From : move.To;
                        string to = redo ? move.To : move.From;
                        File.Move(from, to);
                        succeededMoves.Add(new FileRename { From = from, To = to });
                    }
                    foreach (SavedFile file in files)
                    {
                        string path = redo ? file.AfterPath : file.BeforePath;
                        File.WriteAllBytes(path, redo ? file.After : file.Before);
                    }
                    primitive.SetAppliedFromDetached(redo);
                }
                catch (Exception error)
                {
                    var failures = new List<string>();
                    // Return ORIGINAL content on whichever side currently exists,
                    // then reverse completed moves. Never touch an unknown path.
                    foreach (SavedFile file in files)
                    {
                        try
                        {
                            string beforeReplay = redo ? file.BeforePath : file.AfterPath;
                            string afterReplay = redo ? file.AfterPath : file.BeforePath;
                            string actual = File.Exists(afterReplay) ? afterReplay : beforeReplay;
                            if (File.Exists(actual))
                                File.WriteAllBytes(actual, redo ? file.Before : file.After);
                        }
                        catch (Exception ex) { failures.Add("contenuto: " + ex.Message); }
                    }
                    foreach (FileRename move in succeededMoves.AsEnumerable().Reverse())
                    {
                        try { File.Move(move.To, move.From); }
                        catch (Exception ex) { failures.Add("percorso: " + ex.Message); }
                    }
                    ReopenTabs(dte, closed, false);
                    throw new IOException("U4.B: replay simbolo fallito: " + error.Message +
                        (failures.Count == 0 ? "; file ripristinati." :
                        "; RIPRISTINO PARZIALE: " + string.Join("; ", failures)), error);
                }
                ReopenTabs(dte, closed, true);
                RefreshExplorerFiles(redo ? plan.moves : plan.moves.Select(m =>
                    new FileRename { From = m.To, To = m.From }), null);
                Trace.WriteLine("[Java Symbol U4.B] " + (redo ? "Redo" : "Undo") +
                    " detached completato; file=" + files.Count + "; schede=" + closed.Count);

                var notices = (redo ? plan.moves : plan.moves.Select(m =>
                    new FileRename { From = m.To, To = m.From }))
                    .Select(m => new KeyValuePair<string, string>(m.From, m.To)).ToArray();
                string[] edited = files.Select(f => redo ? f.AfterPath : f.BeforePath).ToArray();
                ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
                {
                    try
                    {
                        string warning;
                        if (notices.Length != 0)
                            warning = await JavaSymbolRenameSync.NotifyAsync(
                                plan.Root, notices, edited);
                        else if (JavaLspRpc.Connected)
                        {
                            var changes = edited.Select(p => new {
                                uri = new Uri(Path.GetFullPath(p)).AbsoluteUri, type = 2
                            }).ToArray();
                            bool sent = await JavaLspRpc.NotifyAsync(
                                "workspace/didChangeWatchedFiles", new { changes });
                            warning = sent ? null : "Invio file changed non riuscito.";
                        }
                        else warning = "Language Server non collegato durante il replay.";
                        if (!string.IsNullOrEmpty(warning))
                            Trace.WriteLine("[Java Symbol U4.B] JDT LS: " + warning);
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine("[Java Symbol U4.B] JDT LS: " + ex);
                    }
                }).FileAndForget("JavaLanguageSupport/SymbolDetachedUndoSync");
            }
        }
    }
}
