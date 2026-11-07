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

        internal static async Task<string> NotifyRenameAsync(string oldDirectory,
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
                    if (!SameParent(source, oldRoot))
                        return "Sorgente esterno al package: sincronizzazione Java non inviata.";
                    notifications.Add(new { uri = FileUri(source), type = 3 });
                }
                notifications.Add(new { uri = DirectoryUri(oldRoot), type = 3 });
                notifications.Add(new { uri = DirectoryUri(newRoot), type = 1 });
                foreach (string source in moved)
                {
                    string destination = Path.Combine(newRoot, Path.GetFileName(source));
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
                return transmitted ? null :
                    "JDT LS si è scollegato: riapri il progetto per aggiornare l'indice Java.";
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

        private static bool SameParent(string file, string parent)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(file));
            return Paths.Equals(directory.TrimEnd('\\', '/'), parent);
        }

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
