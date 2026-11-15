// U4.C - Session-only detached WorkspaceEdit Undo/Redo, based on validated disk snapshots.
// Never reuse a closed ITextDocument or ITextBuffer when undoing a code action.
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
using Microsoft.VisualStudio.Threading;

namespace JavaLanguageSupport.Editor.Refactoring
{
    internal static partial class JavaWorkspaceEditService
    {
        private sealed class WorkspaceEditDetachedState
        {
            // Limits include the BEFORE and AFTER copies for each affected file.
            private const long MaxTotalSnapshotBytes = 16L * 1024 * 1024;

            private sealed class SavedFile
            {
                internal string Path;
                internal byte[] Before;
                internal byte[] After;
            }

            private sealed class OpenTab
            {
                internal Document Document;
                internal string Path;
            }

            private readonly Plan plan;
            private readonly List<SavedFile> files;
            private WorkspaceSaveUndoPrimitive primitive;
            private bool ready;
            private bool disabled;

            private WorkspaceEditDetachedState(Plan plan, List<SavedFile> files)
            {
                this.plan = plan;
                this.files = files;
            }

            internal static WorkspaceEditDetachedState TryCaptureBefore(Plan plan)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                try
                {
                    if (plan == null || plan.Documents.Count == 0 ||
                        plan.Documents.Count > MaximumDocuments) return null;
                    var snapshots = new List<SavedFile>();
                    long total = 0;
                    foreach (DocumentChange item in plan.Documents)
                    {
                        if (item.Buffer == null || item.Document == null ||
                            item.Snapshot == null || item.Document.IsDirty ||
                            item.Document.IsReloading ||
                            !ReferenceEquals(item.Buffer.CurrentSnapshot, item.Snapshot) ||
                            !Paths.Equals(Path.GetFullPath(item.Document.FilePath), item.Path) ||
                            !SafeRegularFile(item.Path)) return null;
                        byte[] before = File.ReadAllBytes(item.Path);
                        if (before.Length > MaxTotalSnapshotBytes - total ||
                            !string.Equals(File.ReadAllText(item.Path), item.Before,
                                StringComparison.Ordinal)) return null;
                        total += before.Length;
                        snapshots.Add(new SavedFile { Path = item.Path, Before = before });
                    }
                    return new WorkspaceEditDetachedState(plan, snapshots);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine("[Java U4.C] Snapshot iniziale detached non disponibile: " + ex);
                    return null;
                }
            }

            internal void Bind(WorkspaceSaveUndoPrimitive value)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                primitive = value;
            }

            // Must be called after every modified ITextDocument.Save() succeeds.
            internal void CompleteAfterSave()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (disabled || primitive == null) return;
                try
                {
                    long total = files.Sum(x => (long)x.Before.Length);
                    foreach (SavedFile file in files)
                    {
                        DocumentChange doc = plan.Documents.First(x => Paths.Equals(x.Path, file.Path));
                        if (doc.Document.IsDirty || doc.Document.IsReloading ||
                            !Paths.Equals(doc.Document.FilePath, file.Path) ||
                            !SafeRegularFile(file.Path))
                            throw new IOException("Documento non salvato: " + file.Path);
                        byte[] after = File.ReadAllBytes(file.Path);
                        if (after.Length > MaxTotalSnapshotBytes - total ||
                            !string.Equals(File.ReadAllText(file.Path),
                                doc.Buffer.CurrentSnapshot.GetText(), StringComparison.Ordinal))
                            throw new IOException("Snapshot finale differente dal buffer: " + file.Path);
                        total += after.Length;
                        file.After = after;
                    }
                    ready = true;
                }
                catch (Exception ex)
                {
                    disabled = true;
                    ready = false;
                    foreach (SavedFile file in files) file.After = null;
                    Trace.WriteLine("[Java U4.C] Replay detached non disponibile: " + ex);
                }
            }

            // All path components must be ordinary directories, not links/junctions.
            private static bool SafeRegularFile(string path)
            {
                try
                {
                    if (!string.Equals(Path.GetExtension(path), ".java",
                        StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
                    FileAttributes attr = File.GetAttributes(path);
                    if ((attr & (FileAttributes.ReadOnly | FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                        return false;
                    for (string dir = Path.GetDirectoryName(Path.GetFullPath(path));
                        !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
                    {
                        if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                            return false;
                    }
                    return true;
                }
                catch (Exception) { return false; }
            }

            // Called often by QueryStatus. Deep validation happens on execution only.
            internal bool CanReplayDetached(bool redo)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return ready && !disabled && primitive != null &&
                    primitive.IsApplied != redo && files.Count != 0 && File.Exists(files[0].Path);
            }

            private void VerifyDisk(bool redo)
            {
                if (!CanReplayDetached(redo))
                    throw new IOException("U4.C: operazione Undo/Redo detached non disponibile.");
                foreach (SavedFile file in files)
                {
                    byte[] expected = redo ? file.Before : file.After;
                    if (!SafeRegularFile(file.Path) || expected == null ||
                        !File.ReadAllBytes(file.Path).SequenceEqual(expected))
                        throw new IOException("U4.C: il file e' cambiato dopo l'azione: " + file.Path);
                }
            }

            private List<OpenTab> PrepareTabs(DTE dte)
            {
                if (dte == null || dte.Documents == null || dte.ItemOperations == null)
                    throw new InvalidOperationException("U4.C: DTE/documenti non disponibili.");
                var model = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
                var adapters = model == null ? null : model.GetService<IVsEditorAdaptersFactoryService>();
                var factory = model == null ? null : model.GetService<ITextDocumentFactoryService>();
                if (adapters == null || factory == null)
                    throw new InvalidOperationException("U4.C: editor/RDT non disponibili.");
                var affected = new HashSet<string>(files.Select(x => x.Path), Paths);
                var seen = new HashSet<string>(Paths);
                var tabs = new List<OpenTab>();
                foreach (Document document in dte.Documents)
                {
                    string path;
                    try { path = Path.GetFullPath(document.FullName); }
                    catch (Exception) { continue; }
                    if (!affected.Contains(path)) continue;
                    if (!seen.Add(path))
                        throw new IOException("U4.C: il documento e' aperto in piu' finestre: " + path);
                    if (!document.Saved)
                        throw new IOException("U4.C: salva prima il documento: " + path);
                    ITextBuffer buffer = OpenBuffer(path, adapters);
                    ITextDocument textDocument;
                    if (buffer == null || !factory.TryGetTextDocument(buffer, out textDocument) ||
                        textDocument == null || textDocument.IsDirty || textDocument.IsReloading ||
                        !Paths.Equals(textDocument.FilePath, path) ||
                        !string.Equals(buffer.CurrentSnapshot.GetText(), File.ReadAllText(path),
                            StringComparison.Ordinal))
                        throw new IOException("U4.C: buffer aperto differente dal disco: " + path);
                    tabs.Add(new OpenTab { Document = document, Path = path });
                }
                return tabs;
            }

            private static void ReopenTabs(DTE dte, IEnumerable<OpenTab> tabs)
            {
                foreach (OpenTab tab in tabs)
                {
                    try
                    {
                        if (File.Exists(tab.Path))
                            dte.ItemOperations.OpenFile(tab.Path, EnvDTE.Constants.vsViewKindTextView);
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine("[Java U4.C] Scheda da riaprire manualmente: " +
                            tab.Path + ": " + ex);
                    }
                }
            }

            internal void ReplayDetached(bool redo)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                VerifyDisk(redo);
                var dte = Package.GetGlobalService(typeof(SDTE)) as DTE;
                List<OpenTab> tabs = PrepareTabs(dte);
                var closed = new List<OpenTab>();
                try
                {
                    foreach (OpenTab tab in tabs)
                    {
                        tab.Document.Close(vsSaveChanges.vsSaveChangesNo);
                        closed.Add(tab);
                    }
                }
                catch (Exception)
                {
                    ReopenTabs(dte, closed);
                    throw;
                }

                var completed = new List<SavedFile>();
                try
                {
                    VerifyDisk(redo); // Another process may have changed files while closing tabs.
                    foreach (SavedFile file in files)
                    {
                        completed.Add(file); // A partial write must also be rolled back.
                        File.WriteAllBytes(file.Path, redo ? file.After : file.Before);
                    }
                    primitive.SetApplied(redo);
                }
                catch (Exception error)
                {
                    var rollbackErrors = new List<string>();
                    foreach (SavedFile file in completed.AsEnumerable().Reverse())
                    {
                        try { File.WriteAllBytes(file.Path, redo ? file.Before : file.After); }
                        catch (Exception rollback) { rollbackErrors.Add(file.Path + ": " + rollback.Message); }
                    }
                    ReopenTabs(dte, closed);
                    throw new IOException("U4.C: Undo/Redo WorkspaceEdit non riuscito: " +
                        error.Message + (rollbackErrors.Count == 0 ? "; file ripristinati." :
                        "; RIPRISTINO PARZIALE: " + string.Join("; ", rollbackErrors)), error);
                }

                ReopenTabs(dte, closed);
                // JDT LS may not see raw disk edits on closed files immediately.
                // This notification is after commit and never rolls back a completed undo.
                string[] changedPaths = files.Select(f => f.Path).ToArray();
                ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
                {
                    try
                    {
                        if (!JavaLspRpc.Connected) return;
                        await JavaLspRpc.NotifyAsync("workspace/didChangeWatchedFiles", new
                        {
                            changes = changedPaths.Select(x => new
                            {
                                uri = new Uri(Path.GetFullPath(x)).AbsoluteUri,
                                type = 2 // LSP FileChangeType.Changed
                            }).ToArray()
                        });
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine("[Java U4.C] Notifica JDT LS non riuscita: " + ex);
                    }
                }).FileAndForget("JavaLanguageSupport/WorkspaceEditDetachedUndo");
                Trace.WriteLine("[Java U4.C] Detached " + (redo ? "Redo" : "Undo") +
                    ": commit; file=" + files.Count + "; schede=" + closed.Count);
            }
        }
    }
}
