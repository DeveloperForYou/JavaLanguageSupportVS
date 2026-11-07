using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using JavaLanguageSupport.LanguageServer;

namespace JavaLanguageSupport.ProjectSystem.Packages
{
    /// <summary>
    /// Inform JDT LS of a completed Java package folder rename. In CPS solution
    /// mode, Open Folder file watchers may not be installed by Visual Studio.
    /// This is a best-effort LSP notification, not proof that indexing completed.
    /// Call ONLY after the disk, editor buffers/RDT and .javaproj commit succeed.
    /// </summary>
    internal static class JavaWorkspaceRenameSync
    {
        private static readonly StringComparer Paths =
            StringComparer.OrdinalIgnoreCase;

        internal static async Task<string> NotifyRenameAsync(string projectFile, string oldDirectory,
            string newDirectory, IEnumerable<string> movedFiles,
            IEnumerable<string> changedFiles)
        {
            try
            {
                if (!JavaLspRpc.Connected)
                    return "JDT LS non è collegato: riapri il progetto per aggiornare l'indice Java.";
                string oldRoot = Path.GetFullPath(oldDirectory).TrimEnd('\\', '/');
                string newRoot = Path.GetFullPath(newDirectory).TrimEnd('\\', '/');
                if (Directory.Exists(oldRoot) || !Directory.Exists(newRoot))
                    return "Le cartelle sono cambiate: impossibile notificare l'indice Java.";

                var moved = (movedFiles ?? Enumerable.Empty<string>())
                    .Select(Path.GetFullPath).Distinct(Paths).ToArray();
                var edited = (changedFiles ?? Enumerable.Empty<string>())
                    .Select(Path.GetFullPath).Distinct(Paths).ToArray();
                if (moved.Length > 500 || edited.Length > 5000)
                    return "Troppi file per una singola notifica di sincronizzazione Java.";

                var notifications = new List<object>();
                // LSP FileChangeType: Created = 1, Changed = 2, Deleted = 3.
                foreach (string source in moved)
                {
                    if (!Inside(oldRoot, source) || Paths.Equals(oldRoot, source))
                        return "Sorgente esterno al package: sincronizzazione Java non inviata.";
                    notifications.Add(new { uri = FileUri(source), type = 3 });
                }
                notifications.Add(new { uri = DirectoryUri(oldRoot), type = 3 });
                notifications.Add(new { uri = DirectoryUri(newRoot), type = 1 });
                foreach (string source in moved)
                {
                    string relative = source.Substring(oldRoot.Length).TrimStart('\\', '/');
                    string destination = Path.Combine(newRoot, relative);
                    if (!File.Exists(destination))
                        return "File Java spostato non trovato: sincronizzazione Java non inviata.";
                    notifications.Add(new { uri = FileUri(destination), type = 1 });
                }
                var movedSet = new HashSet<string>(moved, Paths);
                foreach (string file in edited)
                {
                    // Moved Java files have already received Deleted/Created.
                    if (movedSet.Contains(file)) continue;
                    if (!File.Exists(file))
                        return "Un file modificato non esiste più: sincronizzazione Java non inviata.";
                    notifications.Add(new { uri = FileUri(file), type = 2 });
                }

                bool transmitted = await JavaLspRpc.NotifyAsync(
                    "workspace/didChangeWatchedFiles",
                    new { changes = notifications }).ConfigureAwait(false);
                Trace.WriteLine("[Java CPS] workspace/didChangeWatchedFiles: " +
                    (transmitted ? "inviati " + notifications.Count + " eventi" : "client scollegato"));
                if (!transmitted)
                    return "JDT LS si è scollegato: riapri il progetto per aggiornare l'indice Java.";

                // F2I3-JAVA-MODEL-REFRESH: didChangeWatchedFiles does not
                // guarantee refreshLocal on an Eclipse package directory which
                // was moved by CPS rather than by JDT itself. Ask JDT LS to
                // update its imported project (refreshLocal DEPTH_INFINITE).
                // JDT LS executes this operation as a background WorkspaceJob:
                // successful RPC means scheduled, NOT indexing completed.
                if (string.IsNullOrWhiteSpace(projectFile) ||
                    !string.Equals(Path.GetExtension(projectFile), ".javaproj",
                        StringComparison.OrdinalIgnoreCase) || !File.Exists(projectFile))
                    return "Progetto CPS non disponibile: aggiornamento modello Java non richiesto.";
                string projectRoot = Path.GetDirectoryName(Path.GetFullPath(projectFile));
                if (!Inside(projectRoot, oldRoot) || !Inside(projectRoot, newRoot))
                    return "La rinomina è esterna al progetto Java: aggiornamento modello non richiesto.";
                string projectUri = DirectoryUri(projectRoot);

                // In JDT LS, java.project.changeImportedProjects accepts three
                // lists: project roots to IMPORT, to UPDATE, and to DELETE.
                // The UPDATE list is index 1 in ProjectsManager's dispatcher.
                // Never delete or replace the imported project here.
                bool queued;
                try
                {
                    queued = await JavaLspRpc.ExecuteWorkspaceCommandStrictAsync(
                        "java.project.changeImportedProjects",
                        new object[] { new string[0], new[] { projectUri }, new string[0] })
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine("[Java CPS] Aggiornamento modello Java: " + ex);
                    return "File sincronizzati, ma JDT LS non ha accettato la richiesta " +
                        "di aggiornamento del progetto: " + ex.Message;
                }
                if (!queued)
                    return "Connessione JDT LS interrotta prima dell'aggiornamento del progetto.";
                Trace.WriteLine("[Java CPS] Richiesto aggiornamento modello Java: " + projectUri);
                return null;
            }
            catch (Exception ex)
            {
                // The filesystem operation was already committed. Never report
                // a failed rename because a subsequent notification failed.
                Trace.WriteLine("[Java CPS] Sincronizzazione JDT LS: " + ex);
                return "Package rinominato, ma la sincronizzazione di JDT LS non è riuscita: " +
                    ex.Message + ". Riapri il progetto per aggiornare l'indice.";
            }
        }

        // F2I3-JAVA-MODEL-REFRESH: do not send a refresh for another project.
        private static bool Inside(string root, string candidate)
        {
            string a = Path.GetFullPath(root).TrimEnd('\\', '/');
            string b = Path.GetFullPath(candidate).TrimEnd('\\', '/');
            return Paths.Equals(a, b) ||
                b.StartsWith(a + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
        }

        // F2I4-RECURSIVE-PACKAGE: moved files keep their complete
        // relative paths beneath the renamed root.
        private static string FileUri(string path)
        {
            return new Uri(Path.GetFullPath(path)).AbsoluteUri;
        }

        private static string DirectoryUri(string path)
        {
            string folder = Path.GetFullPath(path).TrimEnd('\\', '/') +
                Path.DirectorySeparatorChar;
            return new Uri(folder).AbsoluteUri;
        }
    }
}
