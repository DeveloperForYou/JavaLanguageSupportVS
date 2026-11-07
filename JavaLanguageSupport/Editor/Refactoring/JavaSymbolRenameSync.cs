using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JavaLanguageSupport.LanguageServer;

namespace JavaLanguageSupport.Editor.Refactoring
{
    /// <summary>
    /// Notifies the existing JDT LS client of a committed .java file rename.
    /// Editor didChange covers text edits, but not necessarily the filesystem
    /// delete/create pair (including the inverse pair on linked Undo/Redo).
    /// A successful RPC schedules a Java-model refresh; it does not prove
    /// that indexing has completed. Never delete the JDT LS cache here.
    /// </summary>
    internal static class JavaSymbolRenameSync
    {
        private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
        private static readonly SemaphoreSlim DispatchGate = new SemaphoreSlim(1, 1);

        internal static async Task<string> NotifyAsync(string projectRoot,
            IEnumerable<KeyValuePair<string, string>> fileMoves,
            IEnumerable<string> editedFiles)
        {
            try
            {
                if (!JavaLspRpc.Connected || !JavaLanguageStatus.IsInitialized)
                    return "JDT LS non e' inizializzato: sincronizzazione rinomina non inviata.";
                if (string.IsNullOrWhiteSpace(projectRoot))
                    return "Root del progetto Java non disponibile.";
                // For plain .javaproj projects JavaWorkspace.Detect(origin) can
                // return the source file's directory, not the CPS project root.
                // Prefer the closest real Java project marker above the moved file.
                var inputMoves = (fileMoves ?? Enumerable.Empty<KeyValuePair<string, string>>())
                    .ToArray();
                if (inputMoves.Length == 0) return null;
                string root = ResolveProjectRoot(projectRoot, inputMoves[0].Key);
                if (!Directory.Exists(root))
                    return "Root del progetto Java non esistente: " + root;

                var moves = inputMoves
                    .Select(x => new KeyValuePair<string, string>(
                        Path.GetFullPath(x.Key), Path.GetFullPath(x.Value))).ToArray();
                if (moves.Length == 0) return null; // Pure symbol edits use VS didChange.
                if (moves.Length > 100 || moves.Select(x => x.Key).Distinct(Paths).Count() != moves.Length)
                    return "Rinomine file duplicate o eccessive: sincronizzazione non inviata.";

                var oldPaths = new HashSet<string>(moves.Select(x => x.Key), Paths);
                var newPaths = new HashSet<string>(moves.Select(x => x.Value), Paths);
                if (newPaths.Count != moves.Length)
                    return "Destinazioni file duplicate: sincronizzazione non inviata.";
                foreach (var move in moves)
                {
                    if (!Inside(root, move.Key) || !Inside(root, move.Value) ||
                        !string.Equals(Path.GetExtension(move.Key), ".java", StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(Path.GetExtension(move.Value), ".java", StringComparison.OrdinalIgnoreCase) ||
                        !Paths.Equals(Path.GetDirectoryName(move.Key), Path.GetDirectoryName(move.Value)))
                        return "Rinomina esterna al progetto o non Java: sincronizzazione non inviata.";
                }

                string[] changed = (editedFiles ?? Enumerable.Empty<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(Path.GetFullPath).Distinct(Paths).ToArray();
                if (changed.Length > 5000)
                    return "Troppi file modificati per la sincronizzazione.";

                // The initial command and Undo/Redo can arrive close together.
                // Serialize, then re-check the committed filesystem state.
                await DispatchGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!JavaLspRpc.Connected || !JavaLanguageStatus.IsInitialized)
                        return "JDT LS non e' collegato durante la sincronizzazione.";
                    foreach (var move in moves)
                        if (File.Exists(move.Key) || !File.Exists(move.Value))
                            return "I percorsi della rinomina sono cambiati: notifica non inviata.";

                    var notifications = new List<object>();
                    // LSP FileChangeType: Created=1, Changed=2, Deleted=3.
                    foreach (var move in moves)
                        notifications.Add(new { uri = FileUri(move.Key), type = 3 });
                    foreach (var move in moves)
                        notifications.Add(new { uri = FileUri(move.Value), type = 1 });
                    foreach (string changedPath in changed)
                    {
                        string actual = changedPath;
                        foreach (var move in moves)
                            if (Paths.Equals(actual, move.Key))
                            {
                                actual = move.Value;
                                break;
                            }
                        if (!Inside(root, actual) || !File.Exists(actual) ||
                            oldPaths.Contains(actual) || newPaths.Contains(actual) ||
                            !string.Equals(Path.GetExtension(actual), ".java",
                                StringComparison.OrdinalIgnoreCase))
                            continue;
                        notifications.Add(new { uri = FileUri(actual), type = 2 });
                    }
                    bool sent = await JavaLspRpc.NotifyAsync(
                        "workspace/didChangeWatchedFiles", new { changes = notifications })
                        .ConfigureAwait(false);
                    if (!sent) return "JDT LS si e' scollegato durante la notifica.";

                    // Package fragments may remain stale even though the disk and
                    // the VS editor have the correct file name. Ask Eclipse to
                    // refresh its imported project model without cache removal.
                    bool queued = await JavaLspRpc.ExecuteWorkspaceCommandStrictAsync(
                        "java.project.changeImportedProjects",
                        new object[] { new string[0], new[] { DirectoryUri(root) }, new string[0] })
                        .ConfigureAwait(false);
                    Trace.WriteLine("[Java Rename] LSP filesystem events=" + notifications.Count +
                        "; model refresh=" + (queued ? "requested" : "not available") + "; " + root);
                    return queued ? null : "Aggiornamento del modello Java non disponibile.";
                }
                finally { DispatchGate.Release(); }
            }
            catch (Exception ex)
            {
                // Disk + linked Undo may already be committed. Never treat a
                // post-commit notification failure as an undoable rename failure.
                Trace.WriteLine("[Java Rename] Errore sincronizzazione JDT LS: " + ex);
                return "Notifica JDT LS non riuscita: " + ex.Message;
            }
        }

        private static string ResolveProjectRoot(string suggestedRoot, string originalFile)
        {
            string fallback = Path.GetFullPath(suggestedRoot).TrimEnd('\\', '/');
            for (var folder = new DirectoryInfo(Path.GetDirectoryName(
                     Path.GetFullPath(originalFile))); folder != null; folder = folder.Parent)
            {
                string location = folder.FullName.TrimEnd('\\', '/');
                if (File.Exists(Path.Combine(location, "pom.xml")) ||
                    File.Exists(Path.Combine(location, "build.gradle")) ||
                    File.Exists(Path.Combine(location, "build.gradle.kts")) ||
                    Directory.GetFiles(location, "*.javaproj", SearchOption.TopDirectoryOnly).Length > 0)
                    return location;
            }
            return fallback;
        }

        private static bool Inside(string root, string path)
        {
            string canonical = Path.GetFullPath(path).TrimEnd('\\', '/');
            return Paths.Equals(root, canonical) ||
                canonical.StartsWith(root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static string FileUri(string file) => new Uri(Path.GetFullPath(file)).AbsoluteUri;

        private static string DirectoryUri(string directory)
        {
            string path = Path.GetFullPath(directory).TrimEnd('\\', '/') +
                Path.DirectorySeparatorChar;
            return new Uri(path).AbsoluteUri;
        }
    }
}
