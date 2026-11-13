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
    /// Inform JDT LS of a completed Java package folder rename. In CPS solution
    /// mode, Open Folder file watchers may not be installed by Visual Studio.
    /// This is a best-effort LSP notification, not proof that indexing completed.
    /// Call ONLY after the disk, editor buffers/RDT and .javaproj commit succeed.
    /// </summary>
    internal static class JavaWorkspaceRenameSync
    {
        private static readonly StringComparer Paths =
            StringComparer.OrdinalIgnoreCase;

        // U3.A: ogni commit CPS costituisce una transizione, non un semplice
        // stato finale. Non scartare transizioni Undo/Redo con il debounce U2.
        private static readonly SemaphoreSlim DispatchGate = new SemaphoreSlim(1, 1);
        private static readonly object PendingGate = new object();
        private static TaskCompletionSource<bool> pendingIdle = CompletedSignal();
        private static int pendingCount;
        private static long sequence;
        private static bool shuttingDown;

        private static TaskCompletionSource<bool> CompletedSignal()
        {
            var value = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            value.TrySetResult(true);
            return value;
        }

        internal static bool IsShuttingDown
        {
            get { lock (PendingGate) return shuttingDown; }
        }

        internal static void BeginSession()
        {
            lock (PendingGate)
            {
                if (pendingCount != 0)
                    throw new InvalidOperationException(
                        "Non riaprire una sessione JDT LS con sincronizzazioni CPS pendenti.");
                shuttingDown = false;
            }
            JavaLanguageStatus.LogLifecycle("sync CPS U3A: nuova sessione");
        }

        // La chiusura attende anche le operazioni in coda, non solo quelle
        // che hanno gia' acquisito DispatchGate.
        internal static async Task<bool> BeginShutdownAndDrainAsync(int milliseconds)
        {
            Task pending;
            lock (PendingGate)
            {
                shuttingDown = true;
                pending = pendingIdle.Task;
            }
            var timeout = Task.Delay(Math.Max(1, milliseconds));
            bool drained = await Task.WhenAny(pending, timeout).ConfigureAwait(false) == pending;
            JavaLanguageStatus.LogLifecycle("sync CPS drain=" + drained);
            return drained;
        }

        // La navigazione non deve interrogare il modello mentre la nostra
        // riconciliazione CPS e' ancora pendente. Non promette la fine dei
        // job di indicizzazione interni a Eclipse.
        internal static async Task<bool> WaitForPendingAsync(int milliseconds,
            CancellationToken cancellationToken)
        {
            Task pending;
            lock (PendingGate) pending = pendingIdle.Task;
            if (pending.IsCompleted) return true;
            var timeout = Task.Delay(Math.Max(1, milliseconds), cancellationToken);
            bool done = await Task.WhenAny(pending, timeout).ConfigureAwait(false) == pending;
            cancellationToken.ThrowIfCancellationRequested();
            return done;
        }

        private static bool TryRegister(out long id)
        {
            lock (PendingGate)
            {
                id = 0;
                if (shuttingDown) return false;
                if (pendingCount++ == 0)
                    pendingIdle = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                id = ++sequence;
                return true;
            }
        }

        private static void FinishRegistered()
        {
            lock (PendingGate)
            {
                if (--pendingCount == 0) pendingIdle.TrySetResult(true);
            }
        }

        internal static async Task<string> NotifyRenameAsync(string projectFile, string oldDirectory,
            string newDirectory, IEnumerable<string> movedFiles,
            IEnumerable<string> changedFiles)
        {
            // Copia la fotografia dei percorsi all'atto del commit: le sequenze
            // differite non devono leggere un IEnumerable mutato in seguito.
            string[] moved = (movedFiles ?? Enumerable.Empty<string>()).ToArray();
            string[] edited = (changedFiles ?? Enumerable.Empty<string>()).ToArray();
            long id;
            if (!TryRegister(out id))
                return "JDT LS in arresto: riconciliazione CPS non eseguita.";
            bool entered = false;
            try
            {
                await DispatchGate.WaitAsync().ConfigureAwait(false);
                entered = true;
                JavaLanguageStatus.LogLifecycle("sync CPS U3A #" + id + ": " +
                    Path.GetFileName(oldDirectory) + " -> " + Path.GetFileName(newDirectory));
                return await NotifyRenameCoreAsync(projectFile, oldDirectory,
                    newDirectory, moved, edited).ConfigureAwait(false);
            }
            finally
            {
                if (entered) DispatchGate.Release();
                FinishRegistered();
            }
        }

        private static async Task<string> NotifyRenameCoreAsync(string projectFile, string oldDirectory,
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
                {
                    // Un Undo/Redo piu' recente ha gia' modificato il disco:
                    // la notifica incrementale precedente sarebbe falsa.
                    // Richiedi una scansione del progetto nel suo stato ATTUALE.
                    JavaLanguageStatus.LogLifecycle(
                        "sync CPS U3A: transizione superata, refresh progetto completo");
                    return await RefreshProjectModelAsync(projectFile, oldRoot, newRoot)
                        .ConfigureAwait(false);
                }

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
                    {
                        JavaLanguageStatus.LogLifecycle(
                            "sync CPS U3A: file spostato nuovamente, refresh progetto completo");
                        return await RefreshProjectModelAsync(projectFile, oldRoot, newRoot)
                            .ConfigureAwait(false);
                    }
                    notifications.Add(new { uri = FileUri(destination), type = 1 });
                }
                var movedSet = new HashSet<string>(moved, Paths);
                foreach (string file in edited)
                {
                    // Moved Java files have already received Deleted/Created.
                    if (movedSet.Contains(file)) continue;
                    if (!File.Exists(file))
                    {
                        JavaLanguageStatus.LogLifecycle(
                            "sync CPS U3A: file modificato non piu' presente, refresh progetto completo");
                        return await RefreshProjectModelAsync(projectFile, oldRoot, newRoot)
                            .ConfigureAwait(false);
                    }
                    notifications.Add(new { uri = FileUri(file), type = 2 });
                }

                bool transmitted = await JavaLspRpc.NotifyAsync(
                    "workspace/didChangeWatchedFiles",
                    new { changes = notifications }).ConfigureAwait(false);
                Trace.WriteLine("[Java CPS] workspace/didChangeWatchedFiles: " +
                    (transmitted ? "inviati " + notifications.Count + " eventi" : "client scollegato"));
                if (!transmitted)
                    return "JDT LS si è scollegato: riapri il progetto per aggiornare l'indice Java.";

                return await RefreshProjectModelAsync(projectFile, oldRoot, newRoot)
                    .ConfigureAwait(false);
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

        // Forza un refresh coerente con i file attualmente presenti sul disco,
        // anche quando un'operazione Undo/Redo precedente e' stata superata.
        // RPC completata significa refresh ACCETTATO, NON indice completato.
        private static async Task<string> RefreshProjectModelAsync(
            string projectFile, string oldRoot, string newRoot)
        {
            if (string.IsNullOrWhiteSpace(projectFile) ||
                !string.Equals(Path.GetExtension(projectFile), ".javaproj",
                    StringComparison.OrdinalIgnoreCase) || !File.Exists(projectFile))
                return "Progetto CPS non disponibile: aggiornamento modello non richiesto.";
            string projectRoot = Path.GetDirectoryName(Path.GetFullPath(projectFile));
            if (!Inside(projectRoot, oldRoot) || !Inside(projectRoot, newRoot))
                return "Rinomina fuori dal progetto: aggiornamento modello non richiesto.";
            string projectUri = DirectoryUri(projectRoot);
            try
            {
                bool queued = await JavaLspRpc.ExecuteWorkspaceCommandStrictAsync(
                    "java.project.changeImportedProjects",
                    new object[] { new string[0], new[] { projectUri }, new string[0] })
                    .ConfigureAwait(false);
                if (!queued)
                    return "Connessione JDT LS interrotta prima dell'aggiornamento modello.";
                JavaLanguageStatus.LogLifecycle(
                    "sync CPS U3A: refresh del modello richiesto per " + projectUri);
                return null;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[Java CPS U3A] Refresh Java model: " + ex);
                return "JDT LS non ha accettato il refresh del modello: " + ex.Message;
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
