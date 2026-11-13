// U3.C.11 - Fallback della cronologia CPS nella stessa sessione VS.
// Quando la scheda di ancoraggio originale viene chiusa, il buffer storico
// NON deve piu' essere usato per spostare file. Questa primitive opera sui
// byte verificati su disco, e rilegge i documenti attualmente aperti.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EnvDTE;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

namespace JavaLanguageSupport.ProjectSystem.Packages
{
    internal static partial class JavaRenamePackageCommand
    {
        private sealed partial class PackageRenameUndoPrimitive
        {
            private sealed class DetachedTab
            {
                internal Document Document;
                internal string OriginalPath;
                internal string TargetPath;
            }

            // QueryStatus e' chiamato molto spesso: qui niente hashing o COM.
            // La verifica completa viene sempre ripetuta PRIMA del commit.
            internal bool CanReplayDetached(bool redo)
            {
                if (lastDirectionIsRedo == redo) return false;
                string from = redo ? plan.OldDirectory : plan.NewDirectory;
                string to = redo ? plan.NewDirectory : plan.OldDirectory;
                return Directory.Exists(from) && !Directory.Exists(to) &&
                    File.Exists(plan.ProjectFile);
            }

            private void VerifyDetachedState(bool redo)
            {
                string from = redo ? plan.OldDirectory : plan.NewDirectory;
                string to = redo ? plan.NewDirectory : plan.OldDirectory;
                if (!Directory.Exists(from) || Directory.Exists(to))
                    throw new IOException("U3C11: cartelle non compatibili con Undo/Redo CPS.");
                var currentDirs = new HashSet<string>(FileComparer);
                string[] currentFiles = EnumeratePackageFiles(from, currentDirs);
                if (!directories.SetEquals(currentDirs) || currentFiles.Length != files.Count)
                    throw new IOException("U3C11: la struttura del package e' cambiata.");
                var expected = files.ToDictionary(f => f.Relative, FileComparer);
                foreach (string path in currentFiles)
                {
                    string relative = path.Substring(from.Length).TrimStart('\\', '/');
                    FileSignature signature;
                    if (!expected.TryGetValue(relative, out signature) ||
                        !string.Equals(HashFile(path), redo ? signature.BeforeHash :
                            signature.AfterHash, StringComparison.Ordinal))
                        throw new IOException("U3C11: file cambiato dopo la rinomina: " + path);
                }
                foreach (DocumentChange change in plan.Files.Where(x =>
                    !Under(plan.OldDirectory, x.Path)))
                {
                    byte[] expectedBytes = redo ? change.Before : change.After;
                    if (!File.Exists(change.Path) ||
                        !File.ReadAllBytes(change.Path).SequenceEqual(expectedBytes))
                        throw new IOException("U3C11: riferimento esterno cambiato: " + change.Path);
                }
                byte[] projectBytes = redo ? plan.ProjectBefore : plan.ProjectAfter;
                if (!File.Exists(plan.ProjectFile) ||
                    !File.ReadAllBytes(plan.ProjectFile).SequenceEqual(projectBytes))
                    throw new IOException("U3C11: file .javaproj cambiato.");
                foreach (string path in plan.Files.Select(x => Under(plan.OldDirectory, x.Path) ?
                    DestinationForMovedFile(plan.OldDirectory, from, x.Path) : x.Path)
                    .Concat(new[] { plan.ProjectFile }))
                {
                    if (File.Exists(path) &&
                        (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                        throw new IOException("U3C11: file di sola lettura: " + path);
                }
            }

            private List<DetachedTab> PrepareDetachedTabs(DTE dte, bool redo)
            {
                if (dte == null || dte.Documents == null || dte.ItemOperations == null)
                    throw new InvalidOperationException("U3C11: DTE/documenti non disponibili.");
                string from = redo ? plan.OldDirectory : plan.NewDirectory;
                string to = redo ? plan.NewDirectory : plan.OldDirectory;
                var external = new HashSet<string>(plan.Files.Where(x =>
                    !Under(plan.OldDirectory, x.Path)).Select(x => x.Path), FileComparer);
                var tabs = new List<DetachedTab>();
                foreach (Document document in dte.Documents)
                {
                    string path;
                    try { path = document.FullName; }
                    catch (Exception) { continue; }
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    try { path = Path.GetFullPath(path); }
                    catch (Exception) { continue; }
                    if (!Under(from, path) && !external.Contains(path) &&
                        !FileComparer.Equals(plan.ProjectFile, path)) continue;
                    if (FileComparer.Equals(plan.ProjectFile, path))
                        throw new IOException("U3C11: chiudere il file .javaproj aperto nell'editor.");
                    if (!document.Saved)
                        throw new IOException("U3C11: salvare prima il documento: " + path);
                    tabs.Add(new DetachedTab {
                        Document = document,
                        OriginalPath = path,
                        TargetPath = Under(from, path) ?
                            DestinationForMovedFile(from, to, path) : path
                    });
                }
                return tabs;
            }

            private static void ReopenDetachedTabs(DTE dte,
                IEnumerable<DetachedTab> tabs, bool target)
            {
                foreach (DetachedTab tab in tabs)
                {
                    string path = target ? tab.TargetPath : tab.OriginalPath;
                    try
                    {
                        if (File.Exists(path))
                            dte.ItemOperations.OpenFile(path,
                                EnvDTE.Constants.vsViewKindTextView);
                    }
                    catch (Exception ex)
                    {
                        JavaLanguageStatus.LogLifecycle("U3C11 scheda non riaperta: " +
                            Path.GetFileName(path) + "; " + ex.Message);
                    }
                }
            }

            internal void ReplayDetached(bool redo)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (!CanReplayDetached(redo))
                    throw new InvalidOperationException("U3C11: Undo/Redo CPS non applicabile.");
                VerifyDetachedState(redo);
                var dte = Package.GetGlobalService(typeof(SDTE)) as DTE;
                List<DetachedTab> tabs = PrepareDetachedTabs(dte, redo);
                string from = redo ? plan.OldDirectory : plan.NewDirectory;
                string to = redo ? plan.NewDirectory : plan.OldDirectory;
                var closed = new List<DetachedTab>();
                // Non scrivere mai direttamente su un buffer VS aperto: le
                // schede interessate e pulite sono richiuse e riaperte.
                try
                {
                    foreach (DetachedTab tab in tabs)
                    {
                        tab.Document.Close(vsSaveChanges.vsSaveChangesNo);
                        closed.Add(tab);
                    }
                }
                catch
                {
                    ReopenDetachedTabs(dte, closed, false);
                    throw;
                }

                bool moved = false, projectTouched = false;
                var changed = new List<KeyValuePair<string, byte[]>>();
                try
                {
                    // Ricontrolla il filesystem dopo la chiusura delle schede.
                    VerifyDetachedState(redo);
                    Directory.Move(from, to);
                    moved = true;
                    foreach (DocumentChange change in plan.Files)
                    {
                        string destination = Under(plan.OldDirectory, change.Path) ?
                            DestinationForMovedFile(plan.OldDirectory, to, change.Path) :
                            change.Path;
                        changed.Add(new KeyValuePair<string, byte[]>(destination,
                            File.ReadAllBytes(destination)));
                        File.WriteAllBytes(destination, redo ? change.After : change.Before);
                    }
                    projectTouched = true;
                    File.WriteAllBytes(plan.ProjectFile,
                        redo ? plan.ProjectAfter : plan.ProjectBefore);
                    lastDirectionIsRedo = redo;
                }
                catch (Exception error)
                {
                    var failures = new List<string>();
                    if (projectTouched)
                    {
                        try { File.WriteAllBytes(plan.ProjectFile,
                            redo ? plan.ProjectBefore : plan.ProjectAfter); }
                        catch (Exception ex) { failures.Add("progetto: " + ex.Message); }
                    }
                    foreach (var item in changed.AsEnumerable().Reverse())
                    {
                        try { File.WriteAllBytes(item.Key, item.Value); }
                        catch (Exception ex) { failures.Add("file: " + ex.Message); }
                    }
                    if (moved)
                    {
                        try { Directory.Move(to, from); }
                        catch (Exception ex) { failures.Add("cartella: " + ex.Message); }
                    }
                    ReopenDetachedTabs(dte, closed, false);
                    throw new IOException("U3C11: Undo/Redo fisico fallito: " + error.Message +
                        (failures.Count == 0 ? "; ripristino eseguito." :
                        "; ripristino PARZIALE: " + string.Join("; ", failures)) +
                        " Backup originario: " + plan.BackupDirectory, error);
                }

                ReopenDetachedTabs(dte, closed, true);
                RefreshExplorer(plan);
                JavaLanguageStatus.LogLifecycle("U3C11 detached " +
                    (redo ? "Redo" : "Undo") + ": commit completato; schede=" + closed.Count);

                string[] movedFiles = plan.MovedJavaFiles.Select(x =>
                    redo ? x : DestinationForMovedFile(
                        plan.OldDirectory, plan.NewDirectory, x)).ToArray();
                string[] editedFiles = plan.Files.Select(x =>
                    Under(plan.OldDirectory, x.Path) && !redo ?
                        DestinationForMovedFile(plan.OldDirectory,
                            plan.NewDirectory, x.Path) : x.Path).ToArray();
                ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
                {
                    string warning = await JavaWorkspaceRenameSync.NotifyRenameAsync(
                        plan.ProjectFile, from, to, movedFiles, editedFiles);
                    if (!string.IsNullOrWhiteSpace(warning))
                        JavaLanguageStatus.LogLifecycle("U3C11 sync CPS: " + warning);
                }).FileAndForget("JavaLanguageSupport/U3C11DetachedSync");
            }
        }
    }
}
