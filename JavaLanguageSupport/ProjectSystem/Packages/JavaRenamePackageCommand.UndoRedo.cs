// Pack U1: annullamento/ripetizione globale della rinomina dei package CPS.
// Il codice e' un'altra parte di JavaRenamePackageCommand; non e' un comando separato.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Threading;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Threading;
using EnvDTE;

namespace JavaLanguageSupport.ProjectSystem.Packages
{
    internal static partial class JavaRenamePackageCommand
    {
        // La cronologia VS ha bisogno di un buffer di testo ancora aperto che
        // rappresenti il package. Non aprire una scheda DTE gia' esistente:
        // gli itemId CPS possono cambiare dopo una rinomina.
        private static void EnsureUndoAnchorOpen(Plan plan, IServiceProvider services)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string anchorPath = plan.MovedJavaFiles.FirstOrDefault();
            if (string.IsNullOrEmpty(anchorPath) || !File.Exists(anchorPath))
                throw new InvalidOperationException("Il package non contiene un file Java di ancoraggio per Annulla/Ripeti.");
            var dte = services.GetService(typeof(SDTE)) as DTE;
            if (dte == null || dte.Documents == null || dte.ItemOperations == null)
                throw new InvalidOperationException("Editor Visual Studio non disponibile per Annulla/Ripeti.");

            foreach (Document open in dte.Documents)
            {
                string path;
                try { path = open.FullName; }
                catch (Exception) { continue; }
                if (string.IsNullOrWhiteSpace(path)) continue;
                string full;
                try { full = Path.GetFullPath(path); }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException ||
                    ex is IOException) { continue; }
                if (FileComparer.Equals(full, anchorPath))
                {
                    open.Activate();
                    return;
                }
            }
            // Si apre esclusivamente un file NON aperto. Questo avviene PRIMA
            // della rinomina; non attraversa mai un vecchio itemId CPS.
            dte.ItemOperations.OpenFile(anchorPath, EnvDTE.Constants.vsViewKindTextView);
        }

        private sealed class PackageRenameUndoPrimitive : ITextUndoPrimitive
        {
            private sealed class FileSignature
            {
                internal string Relative;
                internal string BeforeHash;
                internal string AfterHash;
            }

            private readonly Plan plan;
            private readonly List<FileSignature> files = new List<FileSignature>();
            private readonly HashSet<string> directories = new HashSet<string>(FileComparer);
            private readonly HashSet<string> openPaths;
            private bool lastDirectionIsRedo = true;

            internal PackageRenameUndoPrimitive(Plan source)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                plan = source;
                openPaths = new HashSet<string>(source.OpenFiles.Select(x => x.From), FileComparer);

                if (!Directory.Exists(plan.NewDirectory) || Directory.Exists(plan.OldDirectory))
                    throw new IOException("Impossibile registrare la rinomina nell'Undo: cartelle non coerenti.");

                // Foto crittografica dei file di QUALSIASI tipo, non solo .java:
                // Ctrl+Z non deve spostare inavvertitamente nuovi file o modifiche
                // apportate nel package dopo la rinomina.
                var sources = EnumeratePackageFiles(plan.NewDirectory, directories);
                long totalBytes = 0;
                foreach (string path in sources)
                {
                    totalBytes += new FileInfo(path).Length;
                    if (totalBytes > 512L * 1024 * 1024)
                        throw new NotSupportedException("Package oltre 512 MB: Undo non registrabile in sicurezza.");
                    string relative = path.Substring(plan.NewDirectory.Length).TrimStart('\\', '/');
                    string previous = Path.Combine(plan.OldDirectory, relative);
                    var edited = plan.Files.FirstOrDefault(x => FileComparer.Equals(x.Path, previous));
                    files.Add(new FileSignature {
                        Relative = relative,
                        BeforeHash = edited != null ? Hash(edited.Before) : HashFile(path),
                        AfterHash = HashFile(path)
                    });
                }
                // Non accettare un file editato e spostato che non sia nella foto.
                foreach (var edit in plan.Files.Where(x => Under(plan.OldDirectory, x.Path)))
                {
                    string relative = edit.Path.Substring(plan.OldDirectory.Length).TrimStart('\\', '/');
                    if (!files.Any(x => FileComparer.Equals(x.Relative, relative)))
                        throw new IOException("File modificato non trovato dopo la rinomina: " + edit.Path);
                }
            }

            public ITextUndoTransaction Parent { get; set; }
            public bool CanUndo { get { return true; } }
            public bool CanRedo { get { return true; } }
            public bool CanMerge(ITextUndoPrimitive older) { return false; }
            public ITextUndoPrimitive Merge(ITextUndoPrimitive older)
            {
                throw new NotSupportedException("Non unire la rinomina package a un'altra modifica.");
            }
            public void Do() { Replay(true); }
            public void Undo() { Replay(false); }

            // Attraversamento senza junction/symlink: il rename non deve toccare
            // risorse estranee alla cartella selezionata.
            private static string[] EnumeratePackageFiles(string root,
                HashSet<string> relativeDirectories)
            {
                var pending = new Stack<string>();
                var paths = new List<string>();
                pending.Push(root);
                int folderCount = 0;
                while (pending.Count != 0)
                {
                    string current = pending.Pop();
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Undo non supporta directory collegate: " + current);
                    if (++folderCount > 2000)
                        throw new IOException("Undo non supporta oltre 2000 directory nel package.");
                    relativeDirectories.Add(current.Substring(root.Length).TrimStart('\\', '/'));
                    foreach (string child in Directory.GetDirectories(current)) pending.Push(child);
                    foreach (string file in Directory.GetFiles(current))
                    {
                        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                            throw new IOException("Undo non supporta file collegati: " + file);
                        paths.Add(file);
                        if (paths.Count > 10000)
                            throw new IOException("Undo non supporta oltre 10000 file nel package.");
                    }
                }
                return paths.ToArray();
            }

            private static string Hash(byte[] bytes)
            {
                using (var sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(bytes));
            }
            private static string HashFile(string path)
            {
                using (var sha = SHA256.Create())
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    return BitConverter.ToString(sha.ComputeHash(stream));
            }

            private void VerifyState(bool redo)
            {
                string source = redo ? plan.OldDirectory : plan.NewDirectory;
                string destination = redo ? plan.NewDirectory : plan.OldDirectory;
                if (!Directory.Exists(source) || Directory.Exists(destination))
                    throw new IOException("Undo/Redo package bloccato: directory di origine o destinazione modificata: " + source);

                // Protezione integrale contro nuovi file, eliminazioni e modifiche
                // avvenute dopo l'ultima operazione di rinomina.
                var currentDirectories = new HashSet<string>(FileComparer);
                string[] current = EnumeratePackageFiles(source, currentDirectories);
                if (!directories.SetEquals(currentDirectories))
                    throw new IOException("Undo/Redo package bloccato: struttura delle sottocartelle cambiata.");
                if (current.Length != files.Count)
                    throw new IOException("Undo/Redo package bloccato: numero di file cambiato nel package.");
                var expected = files.ToDictionary(x => x.Relative, FileComparer);
                foreach (string path in current)
                {
                    string relative = path.Substring(source.Length).TrimStart('\\', '/');
                    FileSignature saved;
                    if (!expected.TryGetValue(relative, out saved) ||
                        !string.Equals(HashFile(path), redo ? saved.BeforeHash : saved.AfterHash,
                            StringComparison.Ordinal))
                        throw new IOException("Undo/Redo package bloccato: contenuto cambiato: " + path);
                }

                // File modificati esterni alla cartella spostata.
                foreach (var change in plan.Files.Where(x => !Under(plan.OldDirectory, x.Path)))
                {
                    if (!File.Exists(change.Path) ||
                        !File.ReadAllBytes(change.Path).SequenceEqual(redo ? change.Before : change.After))
                        throw new IOException("Undo/Redo package bloccato: riferimento esterno cambiato: " + change.Path);
                }
                if (!File.Exists(plan.ProjectFile) ||
                    !File.ReadAllBytes(plan.ProjectFile).SequenceEqual(redo ? plan.ProjectBefore : plan.ProjectAfter))
                    throw new IOException("Undo/Redo package bloccato: file .javaproj modificato.");

                // Un documento aperto non deve essere stato spostato indipendentemente
                // da un altro componente. La versione di testo puo' gia' essere stata
                // annullata da Visual Studio nell'ambito della transazione collegata.
                foreach (var live in plan.OpenFiles.Where(x => !FileComparer.Equals(x.From, x.To)))
                {
                    string currentPath = redo ? live.From : live.To;
                    if (!FileComparer.Equals(live.TextDocument.FilePath, currentPath))
                        throw new IOException("Undo/Redo package bloccato: scheda Java spostata: " + currentPath);
                }
            }

            private void Replay(bool redo)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                // VS invokes Do() only for REDO: the initial Apply already moved
                // the folder. Refuse duplicate/unexpected invocations.
                if (lastDirectionIsRedo == redo)
                    throw new InvalidOperationException("Sequenza Undo/Redo del package non coerente.");
                VerifyState(redo); // Nessuna mutazione fino a quando tutto e' verificato.

                string fromDirectory = redo ? plan.OldDirectory : plan.NewDirectory;
                string toDirectory = redo ? plan.NewDirectory : plan.OldDirectory;
                var rdt = Package.GetGlobalService(typeof(SVsRunningDocumentTable))
                    as IVsRunningDocumentTable;
                if (rdt == null) throw new InvalidOperationException("RDT non disponibile durante Undo/Redo.");

                var changed = new List<DocumentChange>();
                var renamed = new List<LiveDocument>();
                bool moved = false;
                bool projectWritten = false;
                try
                {
                    Directory.Move(fromDirectory, toDirectory);
                    moved = true;
                    foreach (var live in plan.OpenFiles.Where(x => !FileComparer.Equals(x.From, x.To)))
                    {
                        RenameLiveDocument(rdt, live,
                            redo ? live.From : live.To, redo ? live.To : live.From);
                        renamed.Add(live);
                    }

                    foreach (var edit in plan.Files.Where(x => !openPaths.Contains(x.Path)))
                    {
                        string path = Under(plan.OldDirectory, edit.Path)
                            ? DestinationForMovedFile(plan.OldDirectory,
                                toDirectory, edit.Path) : edit.Path;
                        changed.Add(edit);
                        File.WriteAllBytes(path, redo ? edit.After : edit.Before);
                    }
                    projectWritten = true;
                    File.WriteAllBytes(plan.ProjectFile, redo ? plan.ProjectAfter : plan.ProjectBefore);
                    lastDirectionIsRedo = redo;
                }
                catch (Exception ex)
                {
                    var restoreErrors = new List<string>();
                    try
                    {
                        if (projectWritten)
                            File.WriteAllBytes(plan.ProjectFile, redo ? plan.ProjectBefore : plan.ProjectAfter);
                    }
                    catch (Exception fail) { restoreErrors.Add("progetto: " + fail.Message); }
                    foreach (var edit in changed.AsEnumerable().Reverse())
                    {
                        try
                        {
                            string path = Under(plan.OldDirectory, edit.Path)
                                ? DestinationForMovedFile(plan.OldDirectory, toDirectory, edit.Path) : edit.Path;
                            File.WriteAllBytes(path, redo ? edit.Before : edit.After);
                        }
                        catch (Exception fail) { restoreErrors.Add("file: " + fail.Message); }
                    }
                    foreach (var live in renamed.AsEnumerable().Reverse())
                    {
                        try { RenameLiveDocument(rdt, live,
                            redo ? live.To : live.From, redo ? live.From : live.To); }
                        catch (Exception fail) { restoreErrors.Add("scheda: " + fail.Message); }
                    }
                    if (moved)
                    {
                        try { Directory.Move(toDirectory, fromDirectory); }
                        catch (Exception fail) { restoreErrors.Add("cartella: " + fail.Message); }
                    }
                    throw new IOException("Undo/Redo della rinomina package non riuscito. " + ex.Message +
                        (restoreErrors.Count != 0 ? " Ripristino PARZIALE: " +
                            string.Join("; ", restoreErrors) : " Modifiche fisiche ripristinate.") +
                        " Backup originale: " + plan.BackupDirectory, ex);
                }

                RefreshExplorer(plan);
                // Il linked undo puo' modificare gli altri buffer DOPO questa
                // primitive. Salvare soltanto dopo il completamento del ciclo VS.
                Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        foreach (var live in plan.OpenFiles)
                        {
                            if (live.TextDocument.IsDirty) live.TextDocument.Save();
                        }
                        // Il pack non deve dire 'OK' se un buffer resta sporco.
                        foreach (var live in plan.OpenFiles)
                        {
                            if (live.TextDocument.IsDirty)
                                throw new IOException("Documento non salvato: " + live.TextDocument.FilePath);
                        }
                        var movedFiles = redo ? plan.MovedJavaFiles.ToArray() :
                            plan.MovedJavaFiles.Select(x => DestinationForMovedFile(
                                plan.OldDirectory, plan.NewDirectory, x)).ToArray();
                        var editedFiles = plan.Files.Select(x =>
                            Under(plan.OldDirectory, x.Path) && !redo
                                ? DestinationForMovedFile(plan.OldDirectory, plan.NewDirectory, x.Path)
                                : x.Path).ToArray();
                        ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
                        {
                            string warning = await JavaWorkspaceRenameSync.NotifyRenameAsync(
                                plan.ProjectFile, fromDirectory, toDirectory, movedFiles, editedFiles);
                            if (!string.IsNullOrEmpty(warning))
                                Trace.WriteLine("[Java CPS package Undo/Redo] " + warning);
                        }).FileAndForget("JavaLanguageSupport/JavaCpsPackageUndoSync");
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine("[Java CPS package Undo/Redo] SALVATAGGIO FALLITO: " + ex);
                        System.Windows.MessageBox.Show(
                            "Il package e' stato spostato, ma almeno una scheda non e' stata salvata.\n" +
                            ex.Message + "\n\nControlla i file prima di proseguire. Backup: " +
                            plan.BackupDirectory, "Java: Undo/Redo package",
                            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    }
                }), DispatcherPriority.ApplicationIdle);
            }
        }
    }
}
