using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.Properties;

namespace JavaLanguageSupport.ProjectSystem.Dependencies
{
    /// <summary>
    /// Java CPS F4.D.2: explicit Plain JAR references. Read-only ZIP-central-directory
    /// indexing on a background task, native virtual CPS tree, no JAR execution.
    /// </summary>
    internal partial class JavaDependenciesTreeProvider
    {
        private readonly object plainJarSync = new object();
        private CancellationTokenSource plainJarCancellation;
        private FileSystemWatcher plainJarWatcher;
        private readonly List<FileSystemWatcher> plainJarExternalWatchers =
            new List<FileSystemWatcher>();
        private Timer plainJarDebounce;
        private string plainJarProjectFile;
        private IList<string> plainJarPaths = new List<string>();
        private string plainJarFingerprint;
        private string plainJarStatus;
        private IList<JavaExternalJarSnapshot> plainJarSnapshots = new List<JavaExternalJarSnapshot>();
        private int plainJarGeneration;
        private bool plainJarDisposed;

        private void SchedulePlainJarRefresh(string backend,
            IProjectVersionedValue<IProjectCatalogSnapshot> change)
        {
            string projectFile = null;
            var references = new List<string>();
            if (string.Equals(backend, "Plain", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var instance = change.Value.Project.ProjectInstance;
                    projectFile = Path.GetFullPath(instance.FullPath);
                    string directory = Path.GetDirectoryName(projectFile);
                    foreach (var item in instance.GetItems("JavaJarReference"))
                    {
                        string path = item.GetMetadataValue("FullPath");
                        if (string.IsNullOrWhiteSpace(path))
                        {
                            string include = item.EvaluatedInclude;
                            path = Path.IsPathRooted(include) ? include :
                                Path.Combine(directory, include);
                        }
                        references.Add(Path.GetFullPath(path));
                    }
                }
                catch (Exception) { return; } // CPS project may still be evaluating.
            }

            if (string.IsNullOrWhiteSpace(projectFile) || !File.Exists(projectFile))
            {
                lock (plainJarSync)
                {
                    if (plainJarDisposed) return;
                    StopPlainJarsLocked();
                    plainJarProjectFile = null;
                    plainJarPaths = new List<string>();
                    plainJarSnapshots = new List<JavaExternalJarSnapshot>();
                    plainJarFingerprint = null;
                    plainJarStatus = null;
                }
                return;
            }

            lock (plainJarSync)
            {
                if (plainJarDisposed) return;
                if (!string.Equals(plainJarProjectFile, projectFile,
                    StringComparison.OrdinalIgnoreCase))
                {
                    StopPlainJarsLocked();
                    plainJarProjectFile = projectFile;
                    plainJarPaths = new List<string>();
                    plainJarFingerprint = null;
                    plainJarSnapshots = new List<JavaExternalJarSnapshot>();
                    WatchPlainJarsLocked(Path.GetDirectoryName(projectFile));
                }
                var resolvedPaths = references.Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                if (!plainJarPaths.SequenceEqual(resolvedPaths, StringComparer.OrdinalIgnoreCase))
                {
                    foreach (var watcher in plainJarExternalWatchers) watcher.Dispose();
                    plainJarExternalWatchers.Clear();
                    WatchExternalJarsLocked(Path.GetDirectoryName(projectFile), resolvedPaths);
                }
                plainJarPaths = resolvedPaths;
            }
            RequestPlainJarSnapshot();
        }

        private void WatchPlainJarsLocked(string projectDirectory)
        {
            if (!Directory.Exists(projectDirectory)) return;
            try
            {
                plainJarDebounce = new Timer(_ => RequestPlainJarSnapshot(), null,
                    Timeout.Infinite, Timeout.Infinite);
                plainJarWatcher = new FileSystemWatcher(projectDirectory)
                {
                    Filter = "*.*",
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite |
                        NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                FileSystemEventHandler changed = (s, e) =>
                {
                    if (IsPlainJarInput(e.FullPath)) DebouncePlainJars();
                };
                RenamedEventHandler renamed = (s, e) =>
                {
                    if (IsPlainJarInput(e.FullPath) || IsPlainJarInput(e.OldFullPath))
                        DebouncePlainJars();
                };
                plainJarWatcher.Changed += changed;
                plainJarWatcher.Created += changed;
                plainJarWatcher.Deleted += changed;
                plainJarWatcher.Renamed += renamed;
            }
            catch (IOException) { /* Reloading CPS still refreshes. */ }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
        }

        // A referenced JAR can be outside the project directory. Watch only the
        // explicitly referenced external directories; never recurse through them.
        private void WatchExternalJarsLocked(string projectRoot, IList<string> paths)
        {
            string prefix = projectRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var externalDirs = paths.Select(Path.GetDirectoryName)
                .Where(d => !string.IsNullOrWhiteSpace(d) &&
                    !string.Equals(d, projectRoot, StringComparison.OrdinalIgnoreCase) &&
                    !d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (string directory in externalDirs)
            {
                if (!Directory.Exists(directory)) continue;
                try
                {
                    var watcher = new FileSystemWatcher(directory)
                    {
                        Filter = "*.jar",
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite |
                            NotifyFilters.Size
                    };
                    FileSystemEventHandler changed = (s, e) => DebouncePlainJars();
                    RenamedEventHandler renamed = (s, e) => DebouncePlainJars();
                    watcher.Changed += changed;
                    watcher.Created += changed;
                    watcher.Deleted += changed;
                    watcher.Renamed += renamed;
                    watcher.EnableRaisingEvents = true;
                    plainJarExternalWatchers.Add(watcher);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (ArgumentException) { }
            }
        }

        private static bool IsPlainJarInput(string path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                (path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith(".javaproj", StringComparison.OrdinalIgnoreCase));
        }

        private void DebouncePlainJars()
        {
            lock (plainJarSync)
            {
                if (plainJarDisposed || plainJarDebounce == null) return;
                try { plainJarDebounce.Change(750, Timeout.Infinite); }
                catch (ObjectDisposedException) { }
            }
        }

        private static string GetPlainJarFingerprint(string projectFile, IList<string> paths)
        {
            var signature = new StringBuilder(projectFile);
            foreach (string path in paths)
            {
                signature.Append('|').Append(path).Append('=');
                if (!File.Exists(path))
                {
                    signature.Append("missing");
                    continue;
                }
                var info = new FileInfo(path);
                signature.Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks);
            }
            return signature.ToString();
        }

        private void RequestPlainJarSnapshot()
        {
            IList<string> paths;
            string syncProjectFile;
            string signature;
            CancellationToken token;
            int generation;
            lock (plainJarSync)
            {
                if (plainJarDisposed || string.IsNullOrEmpty(plainJarProjectFile)) return;
                syncProjectFile = plainJarProjectFile;
                paths = plainJarPaths.ToList();
                try { signature = GetPlainJarFingerprint(plainJarProjectFile, paths); }
                catch (IOException) { return; }
                catch (UnauthorizedAccessException) { return; }
                if (signature == plainJarFingerprint) return;
                plainJarFingerprint = signature;
                if (plainJarCancellation != null)
                {
                    plainJarCancellation.Cancel();
                    plainJarCancellation.Dispose();
                }
                plainJarCancellation = new CancellationTokenSource();
                token = plainJarCancellation.Token;
                generation = ++plainJarGeneration;
                plainJarSnapshots = new List<JavaExternalJarSnapshot>();
                plainJarStatus = paths.Count == 0 ? "Nessun JAR configurato" :
                    "Indicizzazione JAR esterni in corso...";
            }
            dependencyLanguageSync.Signal(syncProjectFile);
            PublishPlainJarTree();
            if (paths.Count == 0) return;

            _ = Task.Run(() =>
            {
                IList<JavaExternalJarSnapshot> result = null;
                string status = null;
                try
                {
                    var list = new List<JavaExternalJarSnapshot>();
                    foreach (string path in paths)
                    {
                        token.ThrowIfCancellationRequested();
                        list.Add(JavaExternalJarIndex.Read(path, token));
                    }
                    result = list;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    status = "Indice JAR non disponibile (" + ex.GetType().Name + ").";
                }
                lock (plainJarSync)
                {
                    if (plainJarDisposed || generation != plainJarGeneration ||
                        token.IsCancellationRequested) return;
                    plainJarSnapshots = result ?? new List<JavaExternalJarSnapshot>();
                    plainJarStatus = status;
                }
                PublishPlainJarTree();
            });
        }

        private void PublishPlainJarTree()
        {
            _ = SubmitTreeUpdateAsync((snapshot, exports, token) =>
            {
                token.ThrowIfCancellationRequested();
                lock (plainJarSync)
                {
                    return Task.FromResult(new TreeUpdateResult(CreateRoot(
                        plainJarDisposed || string.IsNullOrEmpty(plainJarProjectFile)
                            ? null : "Plain")));
                }
            });
        }

        private IProjectTree AddResolvedPlainJars(IProjectTree category)
        {
            // F4D23-CPS-TRACE-PROVIDER
            JavaExternalJarSourceTrace.WriteOnce("tree: provider JAR Esterni eseguito");
            IList<JavaExternalJarSnapshot> snapshots;
            string status;
            string projectFile;
            lock (plainJarSync)
            {
                snapshots = plainJarSnapshots;
                status = plainJarStatus;
                projectFile = plainJarProjectFile;
            }
            // F4D21-JAR-SOURCE-REGISTRY
            JavaExternalJarSourceRegistry.Replace(projectFile, snapshots);
            var icon = KnownMonikers.Library.ToProjectSystemType();
            var classIcon = KnownMonikers.Class.ToProjectSystemType();
            var flags = ProjectTreeFlags.Create(ProjectTreeFlags.Common.VirtualFolder);
            if (!string.IsNullOrEmpty(status))
                return category.Add(NewTree(status, icon: icon, expandedIcon: icon,
                    flags: flags)).Parent;

            var nameCounts = snapshots.GroupBy(j => Path.GetFileName(j.Path),
                StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key,
                    g => g.Count(), StringComparer.OrdinalIgnoreCase);
            // CPS requires every sibling node caption to be unique. A shared
            // transitive JAR may appear under multiple Plain package owners.
            var usedCaptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var jar in snapshots)
            {
                string caption = Path.GetFileName(jar.Path);
                if (nameCounts[caption] > 1)
                {
                    string versionFolder = Path.GetDirectoryName(jar.Path);
                    string artifactFolder = versionFolder == null ? null : Path.GetDirectoryName(versionFolder);
                    string groupFolder = artifactFolder == null ? null : Path.GetDirectoryName(artifactFolder);
                    string managerFolder = groupFolder == null ? null : Path.GetDirectoryName(groupFolder);
                    if (string.Equals(Path.GetFileName(managerFolder), "java-packages",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        // Managed layout: lib/java-packages/group/artifact/version/file.jar.
                        // Show the owning direct dependency, not only the version
                        // directory (which is identical for Jackson XML and YAML).
                        caption += " (" + Path.GetFileName(groupFolder) + ":" +
                            Path.GetFileName(artifactFolder) + ":" +
                            Path.GetFileName(versionFolder) + ")";
                    }
                    else
                        caption += " (" + Path.GetFileName(versionFolder) + ")";
                }
                // Other external JARs may still have the same filename and
                // parent directory label. Keep both nodes without CPS collisions.
                if (!usedCaptions.Add(caption))
                {
                    string originalCaption = caption;
                    int suffix = 2;
                    do { caption = originalCaption + " [" + suffix++ + "]"; }
                    while (!usedCaptions.Add(caption));
                }
                IProjectTree jarNode = NewTree(caption, icon: icon,
                    expandedIcon: icon, flags: flags);
                if (!string.IsNullOrEmpty(jar.Status))
                {
                    jarNode = jarNode.Add(NewTree(jar.Status,
                        icon: icon, expandedIcon: icon, flags: flags)).Parent;
                }
                else
                {
                    int classCount = jar.Packages.Sum(p => p.Value.Count);
                    IProjectTree classes = NewTree("Classi (" + classCount + ")",
                        icon: icon, expandedIcon: icon, flags: flags);
                    foreach (var package in jar.Packages)
                    {
                        IProjectTree folder = NewTree(string.IsNullOrEmpty(package.Key)
                            ? "(package predefinito)" : package.Key,
                            icon: icon, expandedIcon: icon, flags: flags);
                        foreach (string className in package.Value)
                        {
                            // F4D21-JAR-SOURCE-FLAG
                            folder = folder.Add(NewTree(className, icon: classIcon,
                                expandedIcon: classIcon,
                                flags: flags.Union(JavaExternalJarSourceTreeFlags.ClassNode))).Parent;
                        }
                        classes = classes.Add(folder).Parent;
                    }
                    jarNode = jarNode.Add(classes).Parent;
                }
                category = category.Add(jarNode).Parent;
            }
            return category;
        }

        private void StopPlainJarsLocked()
        {
            // F4D21-JAR-SOURCE-CLEANUP
            JavaExternalJarSourceRegistry.Clear(plainJarProjectFile);
            ++plainJarGeneration;
            if (plainJarCancellation != null)
            {
                plainJarCancellation.Cancel();
                plainJarCancellation.Dispose();
                plainJarCancellation = null;
            }
            foreach (var watcher in plainJarExternalWatchers) watcher.Dispose();
            plainJarExternalWatchers.Clear();
            if (plainJarWatcher != null)
            {
                plainJarWatcher.Dispose();
                plainJarWatcher = null;
            }
            if (plainJarDebounce != null)
            {
                plainJarDebounce.Dispose();
                plainJarDebounce = null;
            }
        }

        private void DisposePlainJarResolution()
        {
            lock (plainJarSync)
            {
                plainJarDisposed = true;
                StopPlainJarsLocked();
                plainJarProjectFile = null;
            }
        }
    }

    internal sealed class JavaExternalJarSnapshot
    {
        internal string Path { get; set; }
        internal string Status { get; set; }
        internal SortedDictionary<string, SortedSet<string>> Packages { get; set; }
    }

    internal static class JavaExternalJarIndex
    {
        private const long MaxJarSize = 256L * 1024 * 1024;
        private const int MaxEntries = 50000;
        private const int MaxClasses = 10000;
        private const int MaxPackages = 4000;

        internal static JavaExternalJarSnapshot Read(string path, CancellationToken token)
        {
            var result = new JavaExternalJarSnapshot
            {
                Path = path,
                Packages = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal)
            };
            if (!path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            {
                result.Status = "Riferimento non valido (richiesto .jar)";
                return result;
            }
            if (!File.Exists(path))
            {
                result.Status = "File JAR non trovato";
                return result;
            }
            try
            {
                var info = new FileInfo(path);
                if (info.Length > MaxJarSize)
                {
                    result.Status = "JAR troppo grande per l'indice";
                    return result;
                }
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    if (archive.Entries.Count > MaxEntries)
                        throw new InvalidDataException("Troppi elementi ZIP");
                    int classes = 0;
                    foreach (var entry in archive.Entries)
                    {
                        token.ThrowIfCancellationRequested();
                        string name = entry.FullName;
                        if (!name.EndsWith(".class", StringComparison.Ordinal) ||
                            name.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) ||
                            name.IndexOf('\\') >= 0 || name.Contains("../")) continue;
                        string relative = name.Substring(0, name.Length - 6);
                        int separator = relative.LastIndexOf('/');
                        string simple = separator < 0 ? relative : relative.Substring(separator + 1);
                        if (simple == "module-info" || simple == "package-info" ||
                            simple.IndexOf('$') >= 0 || !IsIdentifier(simple)) continue;
                        string package = separator < 0 ? string.Empty :
                            relative.Substring(0, separator).Replace('/', '.');
                        if (package.Length > 320 || !IsPackage(package)) continue;
                        SortedSet<string> names;
                        if (!result.Packages.TryGetValue(package, out names))
                        {
                            if (result.Packages.Count >= MaxPackages)
                                throw new InvalidDataException("Troppi package JAR");
                            names = new SortedSet<string>(StringComparer.Ordinal);
                            result.Packages.Add(package, names);
                        }
                        if (names.Add(simple) && ++classes > MaxClasses)
                            throw new InvalidDataException("Troppe classi JAR");
                    }
                    if (classes == 0) result.Status = "Nessuna classe di primo livello";
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException ||
                ex is UnauthorizedAccessException)
            {
                result.Packages.Clear();
                result.Status = "Indice non disponibile (" + ex.GetType().Name + ")";
            }
            return result;
        }

        private static bool IsIdentifier(string value)
        {
            if (value.Length == 0 || value.Length > 160 ||
                !(char.IsLetter(value[0]) || value[0] == '_')) return false;
            return value.Skip(1).All(c => char.IsLetterOrDigit(c) || c == '_');
        }

        private static bool IsPackage(string value)
        {
            return value.Length == 0 || value.Split('.').All(IsIdentifier);
        }
    }
}
