using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JavaLanguageSupport.LanguageServer;

namespace JavaLanguageSupport.ProjectSystem.Packages
{
    /// <summary>
    /// Synchronizes successful CPS package/source creation with the existing JDT LS.
    /// No extra LSP client, no synthetic editor and no manual cache deletion.
    /// Events are coalesced per .javaproj to avoid repeated model refreshes when
    /// the package picker creates a folder and a wizard then creates a Java file.
    /// This is best-effort: LSP acceptance is not a proof of completed indexing.
    /// </summary>
    internal static class JavaWorkspaceCreationSync
    {
        private sealed class PendingChanges
        {
            internal readonly HashSet<string> Directories =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal readonly HashSet<string> Files =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, PendingChanges> Pending =
            new Dictionary<string, PendingChanges>(StringComparer.OrdinalIgnoreCase);
        // Serialize refresh commands for separate projects sharing one JDT LS.
        private static readonly SemaphoreSlim DispatchGate = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Capture missing directories BEFORE Directory.CreateDirectory.
        /// Returning only new paths prevents false "Created" events for folders
        /// which existed before the user opened the wizard.
        /// </summary>
        internal static string[] MissingDirectories(string projectFile, string targetDirectory)
        {
            string root, project;
            if (!TryProject(projectFile, out project, out root) ||
                string.IsNullOrWhiteSpace(targetDirectory))
                return new string[0];
            try
            {
                string target = Path.GetFullPath(targetDirectory).TrimEnd('\\', '/');
                if (!Inside(root, target)) return new string[0];
                var missing = new List<string>();
                for (string current = target;
                    !string.Equals(current, root, StringComparison.OrdinalIgnoreCase);
                    current = Path.GetDirectoryName(current))
                {
                    if (string.IsNullOrEmpty(current) || !Inside(root, current))
                        return new string[0];
                    if (!Directory.Exists(current)) missing.Add(current);
                }
                missing.Reverse();
                return missing.ToArray();
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException ||
                ex is NotSupportedException || ex is UnauthorizedAccessException)
            {
                Trace.WriteLine("[Java CPS] Individuazione nuove cartelle: " + ex);
                return new string[0];
            }
        }

        internal static void QueueCreatedDirectories(string projectFile,
            IEnumerable<string> directories)
        {
            QueueCreated(projectFile, directories, null);
        }

        internal static void QueueCreatedFile(string projectFile, string file)
        {
            if (string.IsNullOrWhiteSpace(file)) return;
            QueueCreated(projectFile, null, new[] { file });
        }

        internal static void QueueCreated(string projectFile, IEnumerable<string> directories,
            IEnumerable<string> javaFiles)
        {
            string project, root;
            if (!TryProject(projectFile, out project, out root)) return;
            try
            {
                string[] folders = (directories ?? Enumerable.Empty<string>())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => Path.GetFullPath(s).TrimEnd('\\', '/'))
                    .Where(s => Inside(root, s)).Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                string[] files = (javaFiles ?? Enumerable.Empty<string>())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(Path.GetFullPath)
                    .Where(s => Inside(root, s) &&
                        string.Equals(Path.GetExtension(s), ".java", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (folders.Length == 0 && files.Length == 0) return;
                lock (Gate)
                {
                    PendingChanges batch;
                    bool newlyAdded = !Pending.TryGetValue(project, out batch);
                    if (newlyAdded)
                    {
                        batch = new PendingChanges();
                        Pending.Add(project, batch);
                    }
                    foreach (string folder in folders) batch.Directories.Add(folder);
                    foreach (string file in files) batch.Files.Add(file);
                    if (newlyAdded)
                    {
                        // No Visual Studio UI-thread blocking. FlushAsync catches and
                        // traces failures; the file creation is already committed.
                        PendingChanges scheduledBatch = batch;
                        Task.Run(() => FlushAsync(project, scheduledBatch));
                    }
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException ||
                ex is NotSupportedException || ex is UnauthorizedAccessException)
            {
                Trace.WriteLine("[Java CPS] Accodamento creazione: " + ex);
            }
        }

        private static async Task FlushAsync(string project, PendingChanges batch)
        {
            try
            {
                // A package can be created from the Java item picker immediately
                // before its new source file. Merge these events into one batch.
                await Task.Delay(650).ConfigureAwait(false);
                string[] folders, files;
                lock (Gate)
                {
                    PendingChanges current;
                    if (!Pending.TryGetValue(project, out current) ||
                        !ReferenceEquals(current, batch)) return;
                    Pending.Remove(project);
                    folders = batch.Directories.OrderBy(x => x.Length).ToArray();
                    files = batch.Files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                }
                if (folders.Length > 128 || files.Length > 1000)
                {
                    Trace.WriteLine("[Java CPS] Troppe risorse create in un unico batch: " + project);
                    return;
                }

                await DispatchGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    // The CPS client may have started JDT LS just after the
                    // wizard. Never send notifications during initialize.
                    bool ready = false;
                    for (int i = 0; i < 240; ++i) // up to 60s, no UI blocking
                    {
                        if (JavaLspRpc.Connected && JavaLanguageStatus.IsInitialized)
                        {
                            ready = true;
                            break;
                        }
                        await Task.Delay(250).ConfigureAwait(false);
                    }
                    if (!ready)
                    {
                        Trace.WriteLine("[Java CPS] JDT LS non inizializzato: " +
                            "le nuove risorse saranno importate all'avvio successivo. " + project);
                        return;
                    }
                    string root = Path.GetDirectoryName(project);
                    if (!File.Exists(project)) return;
                    var changes = new List<object>();
                    foreach (string folder in folders)
                        if (Inside(root, folder) && Directory.Exists(folder))
                            changes.Add(new { uri = DirectoryUri(folder), type = 1 });
                    foreach (string file in files)
                        if (Inside(root, file) && File.Exists(file))
                            changes.Add(new { uri = FileUri(file), type = 1 });
                    if (changes.Count == 0) return;

                    bool sent = await JavaLspRpc.NotifyAsync(
                        "workspace/didChangeWatchedFiles",
                        new { changes }).ConfigureAwait(false);
                    if (!sent)
                    {
                        Trace.WriteLine("[Java CPS] Client disconnesso durante la notifica di creazione.");
                        return;
                    }

                    // The external disk change alone does not guarantee that
                    // Eclipse has refreshed parent package fragments. This
                    // schedules refreshLocal(DEPTH_INFINITE) on the imported Java
                    // project. It does not delete or recreate the JDT LS cache.
                    bool refreshed = await JavaLspRpc.ExecuteWorkspaceCommandStrictAsync(
                        "java.project.changeImportedProjects",
                        new object[] { new string[0],
                            new[] { DirectoryUri(root) }, new string[0] })
                        .ConfigureAwait(false);
                    Trace.WriteLine("[Java CPS] Risorse create notificate: " + changes.Count +
                        "; refresh progetto " + (refreshed ? "richiesto" : "non disponibile") +
                        "; " + project);
                }
                finally { DispatchGate.Release(); }
            }
            catch (Exception ex)
            {
                // Creation succeeded on disk: never throw back into a WPF wizard.
                Trace.WriteLine("[Java CPS] Sincronizzazione nuove risorse: " + ex);
            }
        }

        private static bool TryProject(string input, out string project, out string root)
        {
            project = root = null;
            if (string.IsNullOrWhiteSpace(input)) return false;
            try
            {
                project = Path.GetFullPath(input);
                if (!string.Equals(Path.GetExtension(project), ".javaproj",
                    StringComparison.OrdinalIgnoreCase) || !File.Exists(project)) return false;
                root = Path.GetDirectoryName(project).TrimEnd('\\', '/');
                return !string.IsNullOrWhiteSpace(root);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException ||
                ex is NotSupportedException || ex is UnauthorizedAccessException)
            {
                Trace.WriteLine("[Java CPS] Percorso progetto non valido: " + ex);
                return false;
            }
        }

        private static bool Inside(string root, string path)
        {
            return string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static string FileUri(string path)
        {
            return new Uri(Path.GetFullPath(path)).AbsoluteUri;
        }

        private static string DirectoryUri(string path)
        {
            return new Uri(Path.GetFullPath(path).TrimEnd('\\', '/') +
                Path.DirectorySeparatorChar).AbsoluteUri;
        }
    }
}
