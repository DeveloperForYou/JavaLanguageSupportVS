using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;
using EnvDTE;

namespace JavaLanguageSupport.ProjectSystem.Packages
{
    /// <summary>
    /// JDT LS driven rename of Java packages, including nested packages.
    /// Fail-closed: no dirty open documents, no external files or unsupported
    /// LSP resource operations. A backup is saved before any filesystem mutation.
    /// CPS package rename supports recursive refactoring and linked Undo/Redo (Pack U1).
    /// </summary>
    internal static partial class JavaRenamePackageCommand
    {
        private sealed class TextChange
        {
            internal int From, To;
            internal string Replacement;
        }

        private sealed class DocumentChange
        {
            internal string Path;
            internal byte[] Before, After;
            internal int EditCount;
            internal readonly List<string> Preview = new List<string>();
        }

        private sealed class LiveDocument
        {
            internal string From, To;
            internal ITextBuffer Buffer;
            internal ITextDocument TextDocument;
            internal ITextSnapshot Snapshot;
            internal string OriginalText;
        }

        private sealed class Plan
        {
            internal string ProjectFile, OldDirectory, NewDirectory;
            internal string OldName, NewName;
            internal readonly List<DocumentChange> Files = new List<DocumentChange>();
            internal readonly List<string> MovedJavaFiles = new List<string>();
            internal readonly List<LiveDocument> OpenFiles = new List<LiveDocument>();
            // F2H1A-DECLARATION-FALLBACK: separate local declarations from JDT edits.
            internal int LocalPackageDeclarations;
            internal byte[] ProjectBefore, ProjectAfter;
            internal string BackupDirectory;
            internal int EditCount { get { return Files.Sum(x => x.EditCount); } }
        }

        private static int busy;
        private static readonly StringComparer FileComparer = StringComparer.OrdinalIgnoreCase;
        private static readonly UTF8Encoding Utf8Strict = new UTF8Encoding(false, true);

        internal static bool CanExecute(IServiceProvider services)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string project, directory, name, sourceRoot;
            return JavaNewPackageCommand.TryGetSelectedJavaPackage(services,
                out project, out directory, out name, out sourceRoot);
        }

        internal static async Task ExecuteAsync(IServiceProvider services)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
            try
            {
                string project, directory, oldName, sourceRoot;
                if (!JavaNewPackageCommand.TryGetSelectedJavaPackage(services,
                    out project, out directory, out oldName, out sourceRoot)) return;
                var dialog = new JavaPackageRenameDialog(directory, oldName);
                if (dialog.ShowDialog() != true) return;
                string newName = dialog.NewPackageName;
                string newDirectory = Path.Combine(Path.GetDirectoryName(directory),
                    newName.Substring(newName.LastIndexOf('.') + 1));

                // F2I4-RECURSIVE-PACKAGE: rename the selected folder as a
                // whole; all its Java descendants must have consistent packages.
                string[] packageSources = CollectPackageJavaSources(directory, oldName);
                bool recursive = Directory.GetDirectories(directory).Length != 0;
                if (Directory.Exists(newDirectory) || File.Exists(newDirectory))
                    throw new IOException("La destinazione esiste già: " + newDirectory);

                string javaFile = packageSources[0];
                await EnsureJavaLanguageServerAsync(services, javaFile);
                JToken response;
                if (recursive)
                {
                    // The folder-rename protocol sets renameSubpackages=true
                    // inside JDT LS. textDocument/rename does NOT do that.
                    await VerifySourceRootIndexedAsync(sourceRoot);
                    response = await JavaLspRpc.RequestAsync("workspace/willRenameFiles",
                        new { files = new[] { new {
                            oldUri = DirectoryLspUri(directory),
                            newUri = DirectoryLspUri(newDirectory)
                        } } }, CancellationToken.None);
                    if (response == null || response.Type == JTokenType.Null)
                    {
                        // JDT LS can legitimately return no WorkspaceEdit when
                        // VS does not advertise LSP resourceOperations and no
                        // semantic reference changes are required. We still
                        // require a verified imported Java source root; each
                        // declaration is checked locally, and stale references
                        // in all project sources cause a hard failure.
                        response = new JObject { ["changes"] = new JObject() };
                    }
                    else if (response is JObject &&
                        response["changes"] == null && response["documentChanges"] == null)
                        ((JObject)response)["changes"] = new JObject();
                }
                else
                {
                    string anchorFile;
                    int line, column;
                    FindPackageDeclaration(directory, oldName, out anchorFile,
                        out line, out column);
                    response = await JavaLspRpc.RequestAsync("textDocument/rename",
                        new {
                            textDocument = new { uri = new Uri(anchorFile).AbsoluteUri },
                            position = new { line, character = column },
                            newName = newName
                        }, CancellationToken.None);
                    // U3.A single source: JDT LS puo' non produrre un
                    // WorkspaceEdit per un package senza riferimenti esterni.
                    // Verificare prima la source root; Prepare applichera'
                    // solo dichiarazioni locali e rifiutera' riferimenti vecchi.
                    if (response == null || response.Type == JTokenType.Null)
                    {
                        await VerifySourceRootIndexedAsync(sourceRoot);
                        response = new JObject { ["changes"] = new JObject() };
                    }
                }
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                Plan plan = Prepare(response, project, directory, newDirectory,
                    oldName, newName, sourceRoot);
                if (new JavaPackageRenamePreviewDialog(plan).ShowDialog() != true) return;
                // Pack U1: il documento di ancoraggio deve essere aperto PRIMA
                // della rinomina per mantenere una cronologia VS utilizzabile.
                EnsureUndoAnchorOpen(plan, services);
                EnsureReady(plan, services);
                Apply(plan);
                // The physical rename is committed. Notify JDT LS explicitly:
                // CPS Solution Explorer is not necessarily an Open Folder workspace.
                string workspaceNotice = await JavaWorkspaceRenameSync.NotifyRenameAsync(
                    plan.ProjectFile, plan.OldDirectory, plan.NewDirectory, plan.MovedJavaFiles,
                    plan.Files.Select(x => x.Path));
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                Notify(services,
                    "Package rinominato correttamente.\n\n" +
                    "Da: " + oldName + "\nA:  " + newName + "\n\n" +
                    "Schede Java mantenute aperte: " + plan.OpenFiles.Count +
                    "\nFile con modifiche: " + plan.Files.Count +
                    "\nModifiche al codice: " + plan.EditCount +
                    "\n\nBackup: " + plan.BackupDirectory +
                    "\n\nCTRL+Z: annulla la rinomina del package. " +
                    "CTRL+Y: ripeti la rinomina, dalla scheda Java attivata. " +
                    "Non chiudere tale scheda prima di annullare/ripetere. " +
                    "In caso di conflitti utilizza Git o il backup." +
                    (string.IsNullOrWhiteSpace(workspaceNotice) ? "" :
                        "\n\n" + workspaceNotice), false);
            }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                Notify(services, ex.Message, true);
            }
            finally { Interlocked.Exchange(ref busy, 0); }
        }

        // The package context menu must not depend on a Java editor already
        // being open. Opening an existing declaration through Visual Studio's
        // Java editor activates the existing language client on demand.
        // No files are modified and no refactoring is applied during startup.
        private static async Task EnsureJavaLanguageServerAsync(
            IServiceProvider services, string javaFile)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (JavaLspRpc.Connected && JavaLanguageStatus.IsInitialized)
                return;

            if (!File.Exists(javaFile))
                throw new FileNotFoundException(
                    "Il file Java necessario per inizializzare il servizio non esiste.", javaFile);
            var dte = services.GetService(typeof(SDTE)) as DTE;
            if (dte == null || dte.ItemOperations == null)
                throw new InvalidOperationException(
                    "Visual Studio non può aprire un file Java per avviare il servizio linguistico.");
            try
            {
                // This opens a normal tab, without closing any existing Java tabs.
                dte.ItemOperations.OpenFile(javaFile, EnvDTE.Constants.vsViewKindTextView);
            }
            catch (Exception ex) when (ex is COMException || ex is IOException ||
                ex is ArgumentException || ex is UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    "Impossibile aprire il sorgente Java per avviare IntelliSense: " +
                    ex.Message, ex);
            }

            // An established RPC stream is not the same as the completed
            // initialize/initialized LSP handshake. Wait without freezing VS.
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            while (!JavaLspRpc.Connected || !JavaLanguageStatus.IsInitialized)
            {
                if (DateTime.UtcNow >= deadline)
                    throw new InvalidOperationException(
                        "Il servizio Java non ha completato l'avvio. " +
                        "Controlla Strumenti → Java: Stato IntelliSense e riprova. " +
                        "Nessuna rinomina è stata applicata.");
                await Task.Delay(250);
            }
        }

        private static void Notify(IServiceProvider services, string message, bool warning)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            VsShellUtilities.ShowMessageBox(services, message, "Java: Rinomina package",
                warning ? OLEMSGICON.OLEMSGICON_WARNING : OLEMSGICON.OLEMSGICON_INFO,
                OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }

        private static void FindPackageDeclaration(string directory, string expected,
            out string file, out int line, out int column)
        {
            file = null;
            line = 0;
            column = 0;
            foreach (string candidate in Directory.GetFiles(directory, "*.java")
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                string text = ReadUtf8(File.ReadAllBytes(candidate));
                var match = Regex.Match(text,
                    @"(?m)^[ \t]*package[ \t]+" + Regex.Escape(expected) + @"[ \t]*;");
                if (!match.Success) continue;
                // F2H1a: select the final identifier itself, not the ';' boundary.
                int offset = match.Index + match.Value.LastIndexOf(expected,
                    StringComparison.Ordinal) + expected.Length - 1;
                line = 0;
                int lastNewline = -1;
                for (int i = 0; i < offset; ++i)
                    if (text[i] == '\n') { line++; lastNewline = i; }
                column = offset - lastNewline - 1;
                file = candidate;
                return;
            }
            throw new InvalidOperationException(
                "Non trovo un file .java con la dichiarazione 'package " + expected + ";' " +
                "nel package selezionato. Aggiungi almeno un file Java con la dichiarazione corretta.");
        }

        // Verify that Eclipse recognizes exactly the Java source root
        // selected by the CPS package resolver. Fail before any disk mutation.
        private static async Task VerifySourceRootIndexedAsync(string sourceRoot)
        {
            JToken result = await JavaLspRpc.RequestAsync("workspace/executeCommand",
                new { command = "java.project.listSourcePaths",
                    arguments = new object[0] }, CancellationToken.None);
            var paths = result == null ? null : result["data"] as JArray;
            string expected = Path.GetFullPath(sourceRoot).TrimEnd('\\', '/');
            bool found = (bool?)result?["status"] == true && paths != null &&
                paths.Any(x => {
                    string value = (string)x["path"];
                    if (string.IsNullOrWhiteSpace(value)) return false;
                    try
                    {
                        return string.Equals(Path.GetFullPath(value).TrimEnd('\\', '/'),
                            expected, StringComparison.OrdinalIgnoreCase);
                    }
                    catch (Exception ex) when (ex is ArgumentException ||
                        ex is NotSupportedException || ex is PathTooLongException)
                    { return false; }
                });
            if (!found)
                throw new InvalidOperationException(
                    "Il servizio Java non ha ancora importato la source root del package. " +
                    "Attendi l'indicizzazione del progetto Maven/Gradle e riprova. " +
                    "Nessun file è stato modificato.");
        }

        private static string DirectoryLspUri(string directory)
        {
            return new Uri(Path.GetFullPath(directory).TrimEnd('\\', '/') +
                Path.DirectorySeparatorChar).AbsoluteUri;
        }

        // The selected package may be empty but its descendants must contain
        // at least one Java source. Reject links and invalid package folders.
        private static string[] CollectPackageJavaSources(string packageDirectory,
            string packageName)
        {
            string root = Path.GetFullPath(packageDirectory).TrimEnd('\\', '/');
            var pending = new Stack<string>();
            var sources = new List<string>();
            pending.Push(root);
            int visited = 0;
            while (pending.Count != 0)
            {
                string directory = pending.Pop();
                if (++visited > 1000)
                    throw new NotSupportedException(
                        "La struttura del package supera il limite di 1000 cartelle.");
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new NotSupportedException(
                        "La rinomina non supporta cartelle collegate: " + directory);
                string suffix = directory.Substring(root.Length).TrimStart('\\', '/')
                    .Replace('\\', '.').Replace('/', '.');
                if (suffix.Length != 0)
                {
                    string invalid;
                    if (!JavaPackageResolver.TryValidatePackageName(
                        packageName + "." + suffix, out invalid))
                        throw new NotSupportedException(
                            "Il sottopackage non ha un nome Java valido: " + directory);
                }
                foreach (string file in Directory.GetFiles(directory, "*.java"))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                        throw new NotSupportedException("La rinomina non supporta sorgenti collegati: " + file);
                    sources.Add(Path.GetFullPath(file));
                }
                foreach (string child in Directory.GetDirectories(directory))
                    pending.Push(child);
            }
            if (sources.Count == 0)
                throw new NotSupportedException(
                    "Il package e i sottopackage non contengono file Java. " +
                    "Per rinominare cartelle vuote usa Esplora soluzioni.");
            return sources.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static string DestinationForMovedFile(string oldDirectory,
            string newDirectory, string file)
        {
            string relative = Path.GetFullPath(file).Substring(
                Path.GetFullPath(oldDirectory).TrimEnd('\\', '/').Length)
                .TrimStart('\\', '/');
            return Path.Combine(newDirectory, relative);
        }

        private static Plan Prepare(JToken response, string project, string oldDirectory,
            string newDirectory, string oldName, string newName, string sourceRoot)
        {
            var obj = response as JObject;
            if (obj == null) throw new InvalidOperationException(
                "Il servizio Java non ha restituito modifiche applicabili. Controlla l'indicizzazione e riprova.");
            string projectDir = Path.GetDirectoryName(Path.GetFullPath(project));
            oldDirectory = Path.GetFullPath(oldDirectory).TrimEnd('\\', '/');
            newDirectory = Path.GetFullPath(newDirectory).TrimEnd('\\', '/');
            if (!Under(projectDir, oldDirectory) || !Under(sourceRoot, oldDirectory) ||
                !Under(projectDir, newDirectory) || !Under(sourceRoot, newDirectory))
                throw new InvalidOperationException("Rinomina esterna alla source root/progetto.");
            if (!Directory.Exists(oldDirectory) || Directory.Exists(newDirectory))
                throw new InvalidOperationException("Struttura delle cartelle cambiata; riprova.");
            bool recursive = Directory.GetDirectories(oldDirectory).Length > 0;

            var plan = new Plan { ProjectFile = Path.GetFullPath(project),
                OldDirectory = oldDirectory, NewDirectory = newDirectory,
                OldName = oldName, NewName = newName };
            var groups = new Dictionary<string, List<JToken>>(FileComparer);
            var moves = new Dictionary<string, string>(FileComparer);
            bool folderMove = false;
            JArray documents = obj["documentChanges"] as JArray;
            JObject changes = obj["changes"] as JObject;
            if (documents != null && changes != null)
                throw new NotSupportedException("Il servizio Java ha restituito una risposta di rinomina incompatibile. Nessuna modifica applicata.");
            if (documents != null)
            {
                foreach (JToken item in documents)
                {
                    string kind = (string)item["kind"];
                    if (kind == "rename")
                    {
                        string from = LocalPath((string)item["oldUri"]);
                        string to = LocalPath((string)item["newUri"]);
                        if (recursive && FileComparer.Equals(from.TrimEnd(
                            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                            oldDirectory) && FileComparer.Equals(to.TrimEnd(
                            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                            newDirectory) && !folderMove)
                        {
                            folderMove = true;
                            continue;
                        }
                        if (!Under(oldDirectory, from) || !Under(newDirectory, to) ||
                            !FileComparer.Equals(to, DestinationForMovedFile(
                                oldDirectory, newDirectory, from)) ||
                            !string.Equals(Path.GetExtension(from), ".java",
                                StringComparison.OrdinalIgnoreCase) || !File.Exists(from) ||
                            moves.ContainsKey(from))
                            throw new NotSupportedException(
                                "La proposta include uno spostamento di file non supportato: " + from);
                        moves.Add(from, to);
                    }
                    else if (kind == "create" || kind == "delete")
                    {
                        // JDT LS creates/deletes a temporary .temp placeholder to
                        // make the target package directory. Directory.Move already
                        // handles the physical path in this implementation.
                        string marker = LocalPath((string)item["uri"]);
                        if (!string.Equals(Path.GetFileName(marker), ".temp",
                                StringComparison.OrdinalIgnoreCase) ||
                            !(Under(oldDirectory, marker) || Under(newDirectory, marker)))
                            throw new NotSupportedException(
                                "La proposta include la creazione o eliminazione non supportata di: " + marker);
                    }
                    else if (kind == null && item["textDocument"] != null)
                    {
                        AddEdits(groups, ExistingPath((string)item["textDocument"]["uri"],
                            oldDirectory, newDirectory), item["edits"] as JArray);
                    }
                    else throw new NotSupportedException(
                        "La proposta include un'operazione sconosciuta: " + kind);
                }
            }
            else if (changes != null)
            {
                foreach (JProperty entry in changes.Properties())
                    AddEdits(groups, ExistingPath(entry.Name, oldDirectory, newDirectory),
                        entry.Value as JArray);
            }
            else throw new NotSupportedException(
                "Il servizio Java non ha restituito modifiche di rinomina riconoscibili.");

            string[] sourceFiles = CollectPackageJavaSources(oldDirectory, oldName);
            if (sourceFiles.Length == 0)
                throw new NotSupportedException("La rinomina di package vuoti sarà gestita separatamente.");
            if (!folderMove && moves.Count > 0 &&
                (moves.Count != sourceFiles.Length ||
                 sourceFiles.Any(f => !moves.ContainsKey(f))))
                throw new NotSupportedException(
                    "Non tutti i file Java del package risultano inclusi nello spostamento. Nessuna modifica applicata.");
            plan.MovedJavaFiles.AddRange(sourceFiles);
            // U3.A single source: nessun edit LSP non equivale ad errore.
            // Le dichiarazioni locali sono verificate qui sotto e
            // CheckNoStalePackageReferences impedisce refactoring incompleti.

            foreach (var group in groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                string path = Path.GetFullPath(group.Key);
                if (!Under(projectDir, path) ||
                    !string.Equals(Path.GetExtension(path), ".java",
                        StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                    throw new NotSupportedException("La proposta modifica un file esterno al progetto o non Java: " + path);
                byte[] before = File.ReadAllBytes(path);
                string content = ReadUtf8(before);
                var changesForFile = new List<TextChange>();
                foreach (JToken edit in group.Value)
                {
                    if (edit["range"] == null || edit["newText"] == null)
                        throw new InvalidOperationException("TextEdit LSP incompleto: " + path);
                    int start = Offset(content, edit["range"]["start"]);
                    int end = Offset(content, edit["range"]["end"]);
                    if (end < start) throw new InvalidOperationException("Intervallo LSP invertito: " + path);
                    changesForFile.Add(new TextChange { From = start, To = end,
                        Replacement = (string)edit["newText"] });
                }
                changesForFile = changesForFile.GroupBy(x => new { x.From, x.To, x.Replacement })
                    .Select(x => x.First()).OrderByDescending(x => x.From).ToList();
                int previous = content.Length + 1;
                var result = new StringBuilder(content);
                foreach (TextChange edit in changesForFile)
                {
                    if (edit.To > previous || edit.From < 0 || edit.To > content.Length)
                        throw new InvalidOperationException("TextEdit LSP sovrapposti: " + path);
                    result.Remove(edit.From, edit.To - edit.From);
                    result.Insert(edit.From, edit.Replacement);
                    previous = edit.From;
                }
                string afterText = result.ToString();
                // A same-name package may exist in src/test/java: this pack
                // must never rename its declaration without moving its folder.
                if (!Under(oldDirectory, path) &&
                    ContainsPackageOrDescendant(content, oldName) &&
                    ContainsPackageOrDescendant(afterText, newName))
                    throw new NotSupportedException(
                        "La proposta coinvolge anche un'altra radice sorgenti Java: " + path);
                var filePlan = new DocumentChange { Path = path, Before = before,
                    After = WriteUtf8(afterText, HasBom(before)), EditCount = changesForFile.Count };
                foreach (TextChange edit in changesForFile.OrderBy(x => x.From))
                    filePlan.Preview.Add("    " + ShowChange(content.Substring(edit.From,
                        edit.To - edit.From)) + "  →  " + ShowChange(edit.Replacement));
                plan.Files.Add(filePlan);
            }
            // F2H1a: JDT LS may omit RenamePackageChange when the VS native LSP
            // client has not advertised workspaceEdit.resourceOperations. Its
            // independent semantic edits (especially imports) remain authoritative.
            // A package declaration is a local, mechanically verifiable consequence
            // of moving this exact source file; only repair that declaration.
            foreach (string source in sourceFiles)
            {
                var changed = plan.Files.FirstOrDefault(x => FileComparer.Equals(x.Path, source));
                byte[] original = changed == null ? File.ReadAllBytes(source) : changed.Before;
                string content = changed != null ? ReadUtf8(changed.After) : ReadUtf8(original);
                string relativeDir = Path.GetDirectoryName(source).Substring(
                    oldDirectory.Length).TrimStart('\\', '/')
                    .Replace('\\', '.').Replace('/', '.');
                string oldPackage = oldName + (relativeDir.Length == 0 ? "" : "." + relativeDir);
                string newPackage = newName + (relativeDir.Length == 0 ? "" : "." + relativeDir);
                if (!ContainsPackage(content, newPackage))
                {
                    if (!ContainsPackage(content, oldPackage))
                        throw new NotSupportedException(
                            "Dichiarazione package inattesa in " + source +
                            ". Rinomina bloccata senza modifiche.");
                    string repaired = ReplaceSinglePackageDeclaration(content,
                        oldPackage, newPackage);
                    byte[] after = WriteUtf8(repaired, HasBom(original));
                    if (changed == null)
                    {
                        changed = new DocumentChange { Path = source, Before = original,
                            After = after, EditCount = 1 };
                        plan.Files.Add(changed);
                    }
                    else
                    {
                        changed.After = after;
                        changed.EditCount++;
                    }
                    changed.Preview.Add("Dichiarazione package verificata: package " +
                        oldPackage + ";  →  package " + newPackage + ";");
                    plan.LocalPackageDeclarations++;
                }
            }
            // The semantic server remains responsible for imports and fully
            // qualified references. Abort if any old package name survives in
            // Java sources; never silently commit broken references.
            CheckNoStalePackageReferences(plan, projectDir);
            plan.ProjectBefore = File.ReadAllBytes(plan.ProjectFile);
            plan.ProjectAfter = UpdateFolderEntries(plan.ProjectBefore, projectDir,
                oldDirectory, newDirectory);
            return plan;
        }

        // F2H1a: only a single exact declaration, preserving the original file
        // encoding, BOM and line endings. No regex substitutions of imports,
        // qualified names or arbitrary Java syntax are permitted.
        private static string ReplaceSinglePackageDeclaration(string code,
            string oldName, string newName)
        {
            var pattern = new Regex(@"(?m)^[ \t]*package[ \t]+(?<name>" +
                Regex.Escape(oldName) + @")[ \t]*;", RegexOptions.CultureInvariant);
            var found = pattern.Matches(code);
            if (found.Count != 1)
                throw new InvalidOperationException(
                    "Dichiarazione package non univoca: " + oldName);
            var name = found[0].Groups["name"];
            return code.Substring(0, name.Index) + newName +
                code.Substring(name.Index + name.Length);
        }

        private static void CheckNoStalePackageReferences(Plan plan, string projectDir)
        {
            var edited = plan.Files.ToDictionary(x => x.Path, FileComparer);
            var pending = new Stack<string>();
            pending.Push(projectDir);
            int scanned = 0;
            string pattern = @"(?<![\w$.])" + Regex.Escape(plan.OldName) +
                @"(?=[.\s;])";
            // Ignore generated outputs but scan all remaining project Java
            // sources, including test Java, for unhandled imports or FQNs.
            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".git", ".vs", ".gradle", ".idea", "bin", "obj", "target", "build", "out" };
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                foreach (string child in Directory.GetDirectories(directory))
                {
                    if (excluded.Contains(Path.GetFileName(child))) continue;
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                        throw new NotSupportedException(
                            "Il progetto contiene una cartella collegata: " + child +
                            ". Impossibile verificare tutti i riferimenti.");
                    pending.Push(child);
                }
                foreach (string file in Directory.GetFiles(directory, "*.java"))
                {
                    if (++scanned > 10000)
                        throw new NotSupportedException(
                            "Troppi sorgenti da controllare (>10000). Rinomina annullata.");
                    var info = new FileInfo(file);
                    if (info.Length > 16L * 1024 * 1024)
                        throw new NotSupportedException(
                            "Sorgente troppo grande da controllare: " + file);
                    DocumentChange replacement;
                    string text = edited.TryGetValue(Path.GetFullPath(file), out replacement) ?
                        ReadUtf8(replacement.After) : ReadUtf8(File.ReadAllBytes(file));
                    if (Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant))
                        throw new InvalidOperationException(
                            "Restano riferimenti al vecchio package in " + file +
                            ". Non sono stati aggiornati automaticamente: rinomina annullata. " +
                            "Controlla l'indicizzazione e la configurazione del progetto Java.");
                }
            }
        }

        private static string ShowChange(string value)
        {
            if (string.IsNullOrEmpty(value)) return "(inserimento)";
            return value.Replace("\r", "\\r").Replace("\n", "\\n");
        }

        // Avoid changing a package declaration in another source root,
        // including its descendant packages (src/test vs src/main).
        private static bool ContainsPackageOrDescendant(string content, string name)
        {
            return Regex.IsMatch(content, @"(?m)^[ \t]*package[ \t]+" +
                Regex.Escape(name) + @"(?:\.[^\s;]+)*[ \t]*;");
        }

        private static bool ContainsPackage(string content, string name)
        {
            return Regex.IsMatch(content, @"(?m)^[ \t]*package[ \t]+" +
                Regex.Escape(name) + @"[ \t]*;");
        }

        private static int Offset(string content, JToken position)
        {
            if (position == null) throw new InvalidOperationException("Posizione LSP mancante.");
            int line = (int?)position["line"] ?? -1;
            int column = (int?)position["character"] ?? -1;
            if (line < 0 || column < 0)
                throw new InvalidOperationException("Coordinate LSP negative.");
            int index = 0;
            for (int i = 0; i < line; i++)
            {
                index = content.IndexOf('\n', index);
                if (index < 0) throw new InvalidOperationException("Riga LSP inesistente.");
                index++;
            }
            int end = content.IndexOf('\n', index);
            if (end < 0) end = content.Length;
            if (end > index && content[end - 1] == '\r') end--;
            if (column > end - index)
                throw new InvalidOperationException("Colonna LSP fuori intervallo.");
            return index + column;
        }

        private static void AddEdits(Dictionary<string, List<JToken>> groups,
            string path, JArray edits)
        {
            if (edits == null) throw new InvalidOperationException("TextDocumentEdit LSP senza edits.");
            List<JToken> list;
            if (!groups.TryGetValue(path, out list)) groups[path] = list = new List<JToken>();
            list.AddRange(edits);
        }

        private static string ExistingPath(string uri, string oldDirectory, string newDirectory)
        {
            string path = LocalPath(uri);
            if (Under(newDirectory, path))
            {
                string mapped = Path.Combine(oldDirectory,
                    path.Substring(newDirectory.Length).TrimStart('\\', '/'));
                if (File.Exists(mapped)) return Path.GetFullPath(mapped);
            }
            return path;
        }

        private static string LocalPath(string uri)
        {
            Uri parsed;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out parsed) || !parsed.IsFile)
                throw new NotSupportedException("La proposta include un file non locale: " + uri);
            return Path.GetFullPath(parsed.LocalPath);
        }

        private static bool Under(string parent, string child)
        {
            string a = Path.GetFullPath(parent).TrimEnd('\\', '/');
            string b = Path.GetFullPath(child).TrimEnd('\\', '/');
            return FileComparer.Equals(a, b) || b.StartsWith(a + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool DirectChild(string parent, string file)
        {
            return FileComparer.Equals(Path.GetFullPath(parent).TrimEnd('\\', '/'),
                Path.GetDirectoryName(Path.GetFullPath(file)).TrimEnd('\\', '/'));
        }

        private static bool HasBom(byte[] bytes)
        {
            return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        }
        private static string ReadUtf8(byte[] bytes)
        {
            if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) ||
                (bytes[0] == 0xFE && bytes[1] == 0xFF)))
                throw new NotSupportedException("Il file Java usa UTF-16: questa codifica non è supportata dalla rinomina del package.");
            return Utf8Strict.GetString(bytes, HasBom(bytes) ? 3 : 0,
                bytes.Length - (HasBom(bytes) ? 3 : 0));
        }
        private static byte[] WriteUtf8(string value, bool bom)
        {
            byte[] payload = Utf8Strict.GetBytes(value);
            if (!bom) return payload;
            byte[] bytes = new byte[payload.Length + 3];
            bytes[0] = 0xEF; bytes[1] = 0xBB; bytes[2] = 0xBF;
            Buffer.BlockCopy(payload, 0, bytes, 3, payload.Length);
            return bytes;
        }

        private static byte[] UpdateFolderEntries(byte[] original, string projectDirectory,
            string oldDirectory, string newDirectory)
        {
            string xml = ReadUtf8(original);
            string oldRelative = oldDirectory.Substring(
                projectDirectory.TrimEnd('\\', '/').Length).TrimStart('\\', '/')
                .Replace('/', '\\').TrimEnd('\\');
            string newRelative = newDirectory.Substring(
                projectDirectory.TrimEnd('\\', '/').Length).TrimStart('\\', '/')
                .Replace('/', '\\').TrimEnd('\\');
            var include = new Regex(
                "<Folder\\b[^>]*\\bInclude\\s*=\\s*(?<quote>[\\\"'])(?<value>.*?)\\k<quote>",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            string result = include.Replace(xml, match =>
            {
                string raw = match.Groups["value"].Value;
                string normalized = raw.Replace('/', '\\').TrimEnd('\\');
                if (!string.Equals(normalized, oldRelative, StringComparison.OrdinalIgnoreCase) &&
                    !normalized.StartsWith(oldRelative + "\\", StringComparison.OrdinalIgnoreCase))
                    return match.Value;
                string replacement = newRelative + normalized.Substring(oldRelative.Length) +
                    (raw.EndsWith("\\", StringComparison.Ordinal) ? "\\" : "");
                int from = match.Groups["value"].Index - match.Index;
                return match.Value.Substring(0, from) + replacement +
                    match.Value.Substring(from + raw.Length);
            });
            // In F2H.1 we only update explicit Folder items. A manually
            // included JavaCompile/None item under the old path requires a
            // more comprehensive CPS migration: fail rather than break it.
            var document = System.Xml.Linq.XDocument.Parse(result);
            foreach (var element in document.Descendants())
            {
                var attr = element.Attribute("Include");
                if (attr == null || element.Name.LocalName == "Folder") continue;
                string value = attr.Value.Replace('/', '\\');
                if (value.StartsWith(oldRelative + "\\", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException(
                        "Il .javaproj contiene un item esplicito sotto il package: " + value +
                        ". Sono supportati soltanto gli elementi Folder e i sorgenti inclusi automaticamente nel progetto.");
            }
            return WriteUtf8(result, HasBom(original));
        }

        // F2H1B-LIVE-DOCUMENTS: preflight every affected open editor document.
        // Unlike F2H.1, a clean tab stays open; dirty buffers are never overwritten.
        private static void EnsureReady(Plan plan, IServiceProvider services)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dte = services.GetService(typeof(Microsoft.VisualStudio.Shell.Interop.SDTE)) as DTE;
            var model = services.GetService(typeof(Microsoft.VisualStudio.ComponentModelHost.SComponentModel))
                as IComponentModel;
            if (dte == null || dte.Documents == null || model == null)
                throw new InvalidOperationException("Servizi documenti di Visual Studio non disponibili.");
            var adapters = model.GetService<IVsEditorAdaptersFactoryService>();
            var factory = model.GetService<ITextDocumentFactoryService>();
            var rdt = services.GetService(typeof(SVsRunningDocumentTable))
                as IVsRunningDocumentTable;
            if (adapters == null || factory == null || rdt == null)
                throw new InvalidOperationException("RDT o servizi buffer non disponibili.");

            plan.OpenFiles.Clear();
            var affected = new HashSet<string>(plan.Files.Select(x => x.Path), FileComparer);
            foreach (string source in plan.MovedJavaFiles) affected.Add(source);
            affected.Add(plan.ProjectFile);
            var uniqueOpen = new HashSet<string>(FileComparer);
            foreach (Document document in dte.Documents)
            {
                string file;
                try { file = document.FullName; }
                catch (Exception) { continue; }
                if (string.IsNullOrWhiteSpace(file)) continue;
                string full;
                try { full = Path.GetFullPath(file); }
                catch (Exception ex) when (ex is IOException || ex is ArgumentException ||
                    ex is NotSupportedException) { continue; }
                // A non-Java document may also physically reside in the renamed
                // package folder. Refuse instead of leaving its RDT path stale.
                if (!affected.Contains(full) && Under(plan.OldDirectory, full))
                    throw new NotSupportedException(
                        "Chiudi solo il file non Java aperto sotto il package (non supportato): " + full);
                if (!affected.Contains(full) || !uniqueOpen.Add(full)) continue;
                if (FileComparer.Equals(full, plan.ProjectFile))
                    throw new InvalidOperationException(
                        "Il file .javaproj è aperto nell'editor: salvalo e chiudi solo la " +
                        "scheda del progetto prima di procedere. \nI file .java possono restare aperti.");
                if (!document.Saved)
                    throw new InvalidOperationException(
                        "Salva le modifiche a " + full + " e riprova: " +
                        "non occorre chiudere la scheda Java.");

                IVsHierarchy hierarchy;
                uint itemId, cookie;
                IntPtr docData;
                ErrorHandler.ThrowOnFailure(rdt.FindAndLockDocument(
                    (uint)_VSRDTFLAGS.RDT_NoLock, full, out hierarchy,
                    out itemId, out docData, out cookie));
                if (docData == IntPtr.Zero)
                    throw new InvalidOperationException("Buffer aperto non trovato nella RDT: " + full);
                ITextBuffer buffer;
                try
                {
                    var legacy = Marshal.GetObjectForIUnknown(docData) as IVsTextBuffer;
                    if (legacy == null)
                        throw new InvalidOperationException("Documento aperto non testuale: " + full);
                    buffer = adapters.GetDocumentBuffer(legacy) ?? adapters.GetDataBuffer(legacy);
                }
                finally { Marshal.Release(docData); }
                ITextDocument textDocument;
                if (buffer == null || !factory.TryGetTextDocument(buffer, out textDocument) ||
                    textDocument == null ||
                    !FileComparer.Equals(Path.GetFullPath(textDocument.FilePath), full))
                    throw new InvalidOperationException("ITextDocument non disponibile: " + full);
                if (textDocument.IsDirty || textDocument.IsReloading)
                    throw new InvalidOperationException(
                        "Salva il documento aperto e riprova (senza chiuderlo): " + full);
                var snapshot = buffer.CurrentSnapshot;
                byte[] disk = File.ReadAllBytes(full);
                if (!string.Equals(snapshot.GetText(), ReadUtf8(disk), StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Il buffer aperto non corrisponde al file su disco: " + full +
                        ". \nSalva e riprova, senza chiudere la scheda.");
                string target = plan.MovedJavaFiles.Contains(full, FileComparer) ?
                    DestinationForMovedFile(plan.OldDirectory, plan.NewDirectory, full) : full;
                plan.OpenFiles.Add(new LiveDocument { From = full, To = target,
                    Buffer = buffer, TextDocument = textDocument, Snapshot = snapshot,
                    OriginalText = snapshot.GetText() });
            }
            if (Directory.Exists(plan.NewDirectory) || !Directory.Exists(plan.OldDirectory))
                throw new IOException("La directory del package è cambiata dopo l'anteprima.");
            var actualFiles = CollectPackageJavaSources(plan.OldDirectory, plan.OldName);
            if (actualFiles.Length != plan.MovedJavaFiles.Count ||
                actualFiles.Any(x => !plan.MovedJavaFiles.Contains(x, FileComparer)))
                throw new IOException("Elenco dei sorgenti cambiato dopo l'anteprima.");
            foreach (var doc in plan.Files)
                if (!File.ReadAllBytes(doc.Path).SequenceEqual(doc.Before))
                    throw new IOException("Il contenuto è cambiato dopo l'anteprima: " + doc.Path);
            if (!File.ReadAllBytes(plan.ProjectFile).SequenceEqual(plan.ProjectBefore))
                throw new IOException("Il progetto è cambiato dopo l'anteprima.");
            foreach (var doc in plan.Files)
                if ((File.GetAttributes(doc.Path) & FileAttributes.ReadOnly) != 0)
                    throw new IOException("File di sola lettura: " + doc.Path);
            foreach (var file in plan.MovedJavaFiles)
                if ((File.GetAttributes(file) & FileAttributes.ReadOnly) != 0)
                    throw new IOException("File di sola lettura: " + file);
            if ((File.GetAttributes(plan.ProjectFile) & FileAttributes.ReadOnly) != 0)
                throw new IOException("Progetto di sola lettura: " + plan.ProjectFile);
        }

        // Apply the smallest differing span so the editor can track caret/selection.
        // Never write a changed open .java file directly with File.WriteAllBytes.
        private static void ReplaceLiveText(LiveDocument live, string replacement)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string current = live.Buffer.CurrentSnapshot.GetText();
            if (string.Equals(current, replacement, StringComparison.Ordinal)) return;
            int commonStart = 0;
            while (commonStart < current.Length && commonStart < replacement.Length &&
                current[commonStart] == replacement[commonStart]) commonStart++;
            int oldEnd = current.Length, newEnd = replacement.Length;
            while (oldEnd > commonStart && newEnd > commonStart &&
                current[oldEnd - 1] == replacement[newEnd - 1])
            { oldEnd--; newEnd--; }
            using (var edit = live.Buffer.CreateEdit())
            {
                edit.Replace(commonStart, oldEnd - commonStart,
                    replacement.Substring(commonStart, newEnd - commonStart));
                if (edit.Apply() == null)
                    throw new InvalidOperationException("Impossibile aggiornare il buffer: " + live.From);
            }
        }

        private static void RenameLiveDocument(IVsRunningDocumentTable rdt,
            LiveDocument live, string from, string to)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ErrorHandler.ThrowOnFailure(rdt.RenameDocument(from, to,
                VSConstants.HIERARCHY_DONTCHANGE, VSConstants.VSITEMID_NIL));
            try
            {
                if (!FileComparer.Equals(live.TextDocument.FilePath, to))
                    live.TextDocument.Rename(to);
                if (!FileComparer.Equals(live.TextDocument.FilePath, to))
                    throw new IOException("Impossibile aggiornare il percorso della scheda Java: " + to);
            }
            catch
            {
                // Best-effort local rollback even when the RDT path succeeded but
                // ITextDocument.Rename failed. The enclosing transaction also rolls back.
                try { rdt.RenameDocument(to, from,
                    VSConstants.HIERARCHY_DONTCHANGE, VSConstants.VSITEMID_NIL); }
                catch (Exception) { }
                try { if (!FileComparer.Equals(live.TextDocument.FilePath, from))
                    live.TextDocument.Rename(from); }
                catch (Exception) { }
                throw;
            }
        }

        private static void RefreshExplorer(Plan plan)
        {
            try
            {
                var changes = Package.GetGlobalService(typeof(SVsFileChangeEx))
                    as IVsFileChangeEx;
                if (changes == null) return;
                foreach (string from in plan.MovedJavaFiles)
                {
                    changes.SyncFile(from);
                    changes.SyncFile(DestinationForMovedFile(
                        plan.OldDirectory, plan.NewDirectory, from));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("[Java package: aggiornamento Esplora soluzioni] " + ex);
            }
        }

        private static void Apply(Plan plan)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Live buffers, rename notifications and disk writes run on the UI thread.
            // Pack U1: le modifiche di testo avvengono in una transazione VS
            // collegata e una primitive gestisce cartella, file chiusi, CPS e RDT.
            var rdt = Package.GetGlobalService(typeof(SVsRunningDocumentTable))
                as IVsRunningDocumentTable;
            if (rdt == null) throw new InvalidOperationException("RDT non disponibile.");
            var dte = Package.GetGlobalService(typeof(SDTE)) as DTE;
            var model = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
            var registry = model == null ? null : model.GetService<ITextUndoHistoryRegistry>();
            if (dte == null || dte.UndoContext == null || dte.UndoContext.IsOpen ||
                registry == null)
                throw new InvalidOperationException("Undo globale Java CPS non disponibile o già in corso.");
            string anchorPath = plan.MovedJavaFiles.FirstOrDefault();
            var anchor = plan.OpenFiles.FirstOrDefault(x => FileComparer.Equals(x.From, anchorPath));
            if (anchor == null)
                throw new InvalidOperationException("Apri un sorgente Java nel package per poter usare CTRL+Z/CTRL+Y.");
            var history = registry.GetHistory(anchor.Buffer);
            if (history == null || plan.OpenFiles.Any(x => registry.GetHistory(x.Buffer) == null))
                throw new InvalidOperationException("Cronologia Undo Java non disponibile: rinomina annullata.");
            foreach (var live in plan.OpenFiles)
            {
                if (!ReferenceEquals(live.Buffer.CurrentSnapshot, live.Snapshot) ||
                    live.TextDocument.IsDirty ||
                    !FileComparer.Equals(live.TextDocument.FilePath, live.From))
                    throw new IOException("Il documento è cambiato dopo l'anteprima: " + live.From);
            }

            // Persist backups before any disk or buffer mutation.
            string projectDir = Path.GetDirectoryName(plan.ProjectFile);
            string backupRoot = Path.Combine(projectDir, ".vs", "JavaPackageRenameBackups",
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            foreach (var doc in plan.Files)
            {
                string relative = doc.Path.Substring(projectDir.TrimEnd('\\', '/').Length)
                    .TrimStart('\\', '/');
                string destination = Path.Combine(backupRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.WriteAllBytes(destination, doc.Before);
            }
            Directory.CreateDirectory(backupRoot);
            File.WriteAllBytes(Path.Combine(backupRoot, "original.javaproj"), plan.ProjectBefore);
            File.WriteAllText(Path.Combine(backupRoot, "README.txt"),
                "Backup rinomina package Java\r\nOLD: " + plan.OldDirectory +
                "\r\nNEW: " + plan.NewDirectory +
                "\r\nProject: " + plan.ProjectFile + "\r\n",
                new UTF8Encoding(false));
            plan.BackupDirectory = backupRoot;

            var written = new List<DocumentChange>();
            var renamed = new List<LiveDocument>();
            bool moved = false;
            bool linkedOpen = false;
            ITextUndoTransaction packageUndo = null;
            PackageRenameUndoPrimitive globalPrimitive = null;
            try
            {
                dte.UndoContext.Open("Java: Rinomina package " + plan.OldName + " -> " +
                    plan.NewName, true);
                linkedOpen = true;
                packageUndo = history.CreateTransaction("Java: rinomina package CPS");
                foreach (var doc in plan.Files)
                {
                    var live = plan.OpenFiles.FirstOrDefault(x => FileComparer.Equals(x.From, doc.Path));
                    written.Add(doc); // A partial write must also be restored.
                    if (live == null)
                    {
                        File.WriteAllBytes(doc.Path, doc.After);
                    }
                    else
                    {
                        ReplaceLiveText(live, ReadUtf8(doc.After));
                        live.TextDocument.Save();
                        if (live.TextDocument.IsDirty ||
                            !File.ReadAllBytes(doc.Path).SequenceEqual(doc.After))
                            throw new IOException("Salvataggio del buffer Java non coerente: " + doc.Path);
                    }
                }
                // Physical folder rename: all non-Java resources follow the package.
                Directory.Move(plan.OldDirectory, plan.NewDirectory);
                moved = true;
                foreach (var live in plan.OpenFiles.Where(x => !FileComparer.Equals(x.From, x.To)))
                {
                    RenameLiveDocument(rdt, live, live.From, live.To);
                    renamed.Add(live);
                }
                if (!plan.ProjectBefore.SequenceEqual(plan.ProjectAfter))
                    File.WriteAllBytes(plan.ProjectFile, plan.ProjectAfter);

                // Registro lo spostamento e i file non-UI sulla stessa cronologia
                // collegata dei buffer: un singolo CTRL+Z deve annullare tutto.
                globalPrimitive = new PackageRenameUndoPrimitive(plan);
                packageUndo.AddUndo(globalPrimitive);
                packageUndo.Complete();
                packageUndo.Dispose();
                packageUndo = null;
                dte.UndoContext.Close();
                linkedOpen = false;
                // U3.C: registra la STESSA transazione collegata all'Undo
                // globale, non una seconda operazione che sposta file.
                // Un problema nel bridge non deve invalidare il commit CPS.
                try
                {
                    JavaPackageGlobalUndo.Remember(history, globalPrimitive,
                        () => globalPrimitive.IsApplied, plan.OldName, plan.NewName);
                }
                catch (Exception globalError)
                {
                    System.Diagnostics.Trace.WriteLine(
                        "[Java U3.C] Undo globale non registrato: " + globalError);
                }
            }
            catch (Exception error)
            {
                var rollbackErrors = new List<string>();
                if (packageUndo != null)
                {
                    try { packageUndo.Cancel(); packageUndo.Dispose(); }
                    catch (Exception ex) { rollbackErrors.Add("undo: " + ex.Message); }
                }
                if (linkedOpen)
                {
                    try { dte.UndoContext.SetAborted(); dte.UndoContext.Close(); }
                    catch (Exception ex) { rollbackErrors.Add("undo globale: " + ex.Message); }
                }
                // RDT document paths must match their original file names again.
                foreach (var live in renamed.AsEnumerable().Reverse())
                {
                    try { RenameLiveDocument(rdt, live, live.To, live.From); }
                    catch (Exception ex) { rollbackErrors.Add("scheda " + live.From + ": " + ex.Message); }
                }
                if (moved)
                {
                    try { Directory.Move(plan.NewDirectory, plan.OldDirectory); }
                    catch (Exception ex) { rollbackErrors.Add("directory: " + ex.Message); }
                }
                foreach (var doc in written.AsEnumerable().Reverse())
                {
                    try
                    {
                        var live = plan.OpenFiles.FirstOrDefault(x => FileComparer.Equals(x.From, doc.Path));
                        if (live == null)
                            File.WriteAllBytes(doc.Path, doc.Before);
                        else
                        {
                            ReplaceLiveText(live, live.OriginalText);
                            live.TextDocument.Save();
                            if (!File.ReadAllBytes(doc.Path).SequenceEqual(doc.Before))
                                throw new IOException("Ripristino file non coerente.");
                        }
                    }
                    catch (Exception ex) { rollbackErrors.Add(doc.Path + ": " + ex.Message); }
                }
                try { File.WriteAllBytes(plan.ProjectFile, plan.ProjectBefore); }
                catch (Exception ex) { rollbackErrors.Add("progetto: " + ex.Message); }
                throw new InvalidOperationException(
                    "Rinomina non riuscita: " + error.Message +
                    (rollbackErrors.Count > 0 ?
                        "\nRipristino INCOMPLETO: " + string.Join("; ", rollbackErrors) :
                        "\nModifiche ripristinate.") +
                    "\nBackup: " + backupRoot, error);
            }
            RefreshExplorer(plan);
        }

        private static bool ReservedWindowsName(string name)
        {
            string upper = name.ToUpperInvariant();
            if (upper == "CON" || upper == "PRN" || upper == "AUX" || upper == "NUL")
                return true;
            return upper.Length == 4 &&
                (upper.StartsWith("COM", StringComparison.Ordinal) ||
                 upper.StartsWith("LPT", StringComparison.Ordinal)) &&
                upper[3] >= '1' && upper[3] <= '9';
        }

        private static TextBlock SectionLabel(string label)
        {
            var result = Text(label, 11, true);
            return result;
        }

        private static TextBlock Text(string value, double size, bool bold)
        {
            var result = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap,
                FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
            result.SetResourceReference(TextBlock.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            return result;
        }

        private static void Theme(Control control)
        {
            control.SetResourceReference(Control.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            control.SetResourceReference(Control.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            control.SetResourceReference(Control.BorderBrushProperty,
                EnvironmentColors.ToolWindowBorderBrushKey);
        }
    }
}
