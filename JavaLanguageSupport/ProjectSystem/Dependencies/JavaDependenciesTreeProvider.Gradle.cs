using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.Properties;
using Newtonsoft.Json.Linq;

namespace JavaLanguageSupport.ProjectSystem.Dependencies
{
    /// <summary>
    /// Read-only Gradle dependency tree. A Gradle init script resolves the model;
    /// no build.gradle(.kts) or .javaproj is modified by this provider.
    /// </summary>
    internal partial class JavaDependenciesTreeProvider
    {
        private readonly object gradleSync = new object();
        private CancellationTokenSource gradleCancellation;
        private readonly List<FileSystemWatcher> gradleWatchers = new List<FileSystemWatcher>();
        private Timer gradleDebounce;
        private string gradleProjectFile;
        private string gradleCommand;
        private string gradleFingerprint;
        private string gradleStatus;
        private IList<JavaGradleDependencyConfiguration> gradleConfigurations =
            new List<JavaGradleDependencyConfiguration>();
        private int gradleGeneration;
        private bool gradleDisposed;

        private void ScheduleGradleRefresh(string backend,
            IProjectVersionedValue<IProjectCatalogSnapshot> change)
        {
            string projectFile = null;
            string command = null;
            if (string.Equals(backend, "Gradle", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var instance = change.Value.Project.ProjectInstance;
                    projectFile = Path.GetFullPath(instance.FullPath);
                    command = instance.GetPropertyValue("JavaGradleCommand");
                }
                catch (Exception) { return; } // Evaluation not ready yet.
            }

            if (string.IsNullOrWhiteSpace(projectFile) || !File.Exists(projectFile))
            {
                lock (gradleSync)
                {
                    if (gradleDisposed) return;
                    StopGradleLocked();
                    gradleProjectFile = null;
                    gradleFingerprint = null;
                    gradleStatus = null;
                    gradleConfigurations = new List<JavaGradleDependencyConfiguration>();
                }
                return;
            }

            lock (gradleSync)
            {
                if (gradleDisposed) return;
                if (!string.Equals(gradleProjectFile, projectFile,
                    StringComparison.OrdinalIgnoreCase))
                {
                    StopGradleLocked();
                    gradleProjectFile = projectFile;
                    gradleFingerprint = null;
                    gradleConfigurations = new List<JavaGradleDependencyConfiguration>();
                    WatchGradleLocked(Path.GetDirectoryName(projectFile));
                }
                gradleCommand = command;
            }
            RequestGradleSnapshot();
        }

        private static readonly string[] GradleInputFiles =
        {
            "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts",
            "gradle.properties", Path.Combine("gradle", "libs.versions.toml")
        };

        private static string GetGradleFingerprint(string projectFile, string command)
        {
            var root = Path.GetDirectoryName(projectFile);
            var signature = new StringBuilder(projectFile).Append('|').Append(command ?? "");
            foreach (string relative in GradleInputFiles)
            {
                string file = Path.Combine(root, relative);
                signature.Append('|').Append(relative).Append('=');
                if (!File.Exists(file))
                {
                    signature.Append("missing");
                    continue;
                }
                var info = new FileInfo(file);
                signature.Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks);
            }
            return signature.ToString();
        }

        private void WatchGradleLocked(string root)
        {
            if (!Directory.Exists(root)) return;
            gradleDebounce = new Timer(_ => RequestGradleSnapshot(), null,
                Timeout.Infinite, Timeout.Infinite);
            AddGradleWatcherLocked(root, false);
            string catalog = Path.Combine(root, "gradle");
            if (Directory.Exists(catalog)) AddGradleWatcherLocked(catalog, true);
        }

        private void AddGradleWatcherLocked(string folder, bool catalog)
        {
            try
            {
                var watcher = new FileSystemWatcher(folder)
                {
                    Filter = "*.*",
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                FileSystemEventHandler changed = (s, e) =>
                {
                    if (IsGradleInput(e.Name, catalog)) DebounceGradle();
                };
                RenamedEventHandler renamed = (s, e) =>
                {
                    if (IsGradleInput(e.Name, catalog) || IsGradleInput(e.OldName, catalog))
                        DebounceGradle();
                };
                watcher.Changed += changed;
                watcher.Created += changed;
                watcher.Deleted += changed;
                watcher.Renamed += renamed;
                watcher.EnableRaisingEvents = true;
                gradleWatchers.Add(watcher);
            }
            catch (IOException) { /* File watching is optional; CPS reload still refreshes. */ }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { /* Directory may disappear during reload. */ }
        }

        private static bool IsGradleInput(string file, bool catalog)
        {
            if (catalog)
                return string.Equals(file, "libs.versions.toml", StringComparison.OrdinalIgnoreCase);
            return GradleInputFiles.Take(5).Any(input =>
                string.Equals(file, input, StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(file, "gradle", StringComparison.OrdinalIgnoreCase);
        }

        private void DebounceGradle()
        {
            lock (gradleSync)
            {
                if (gradleDisposed || gradleDebounce == null) return;
                try { gradleDebounce.Change(900, Timeout.Infinite); }
                catch (ObjectDisposedException) { }
            }
        }

        private void RequestGradleSnapshot()
        {
            string projectFile, command, fingerprint;
            CancellationToken token;
            int generation;
            lock (gradleSync)
            {
                if (gradleDisposed || string.IsNullOrEmpty(gradleProjectFile)) return;
                projectFile = gradleProjectFile;
                command = gradleCommand;
                // A version-catalog folder may be created after project load.
                string catalog = Path.Combine(Path.GetDirectoryName(projectFile), "gradle");
                if (Directory.Exists(catalog) && !gradleWatchers.Any(w =>
                    string.Equals(w.Path, catalog, StringComparison.OrdinalIgnoreCase)))
                    AddGradleWatcherLocked(catalog, true);
                try { fingerprint = GetGradleFingerprint(projectFile, command); }
                catch (IOException) { return; }
                catch (UnauthorizedAccessException) { return; }
                if (fingerprint == gradleFingerprint) return;
                gradleFingerprint = fingerprint;
                if (gradleCancellation != null)
                {
                    gradleCancellation.Cancel();
                    gradleCancellation.Dispose();
                }
                gradleCancellation = new CancellationTokenSource();
                token = gradleCancellation.Token;
                generation = ++gradleGeneration;
                gradleStatus = "Risoluzione delle dipendenze Gradle in corso...";
                gradleConfigurations = new List<JavaGradleDependencyConfiguration>();
            }

            dependencyLanguageSync.Signal(projectFile);
            PublishGradleTree();
            _ = Task.Run(() =>
            {
                IList<JavaGradleDependencyConfiguration> configurations = null;
                string status = null;
                try
                {
                    configurations = JavaGradleDependencyResolver.Resolve(projectFile, command, token);
                    if (configurations.Count == 0)
                        status = "Nessuna configurazione Gradle risolvibile";
                    else if (configurations.All(c => c.Children.Count == 0))
                        status = "Nessuna dipendenza Gradle";
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    // No Gradle stdout/stderr, paths or potentially sensitive values in tree.
                    status = "Dipendenze Gradle non disponibili (" +
                        ex.GetType().Name + "). Vedere README F4.C.1.";
                }
                lock (gradleSync)
                {
                    if (gradleDisposed || generation != gradleGeneration ||
                        token.IsCancellationRequested) return;
                    gradleConfigurations = configurations ??
                        new List<JavaGradleDependencyConfiguration>();
                    gradleStatus = status;
                }
                PublishGradleTree();
            });
        }

        private void PublishGradleTree()
        {
            _ = SubmitTreeUpdateAsync((snapshot, exports, token) =>
            {
                token.ThrowIfCancellationRequested();
                lock (gradleSync)
                {
                    if (gradleDisposed || string.IsNullOrWhiteSpace(gradleProjectFile))
                        return Task.FromResult(new TreeUpdateResult(CreateRoot(null)));
                    return Task.FromResult(new TreeUpdateResult(CreateRoot("Gradle")));
                }
            });
        }

        private IProjectTree AddResolvedGradleDependencies(IProjectTree category)
        {
            IList<JavaGradleDependencyConfiguration> configurations;
            string status;
            lock (gradleSync)
            {
                configurations = gradleConfigurations;
                status = gradleStatus;
            }
            var icon = KnownMonikers.Library.ToProjectSystemType();
            var flags = ProjectTreeFlags.Create(ProjectTreeFlags.Common.VirtualFolder);
            if (status != null)
                return category.Add(NewTree(status, icon: icon, expandedIcon: icon,
                    flags: flags)).Parent;

            foreach (var configuration in configurations)
            {
                IProjectTree group = NewTree(configuration.Name, icon: icon,
                    expandedIcon: icon, flags: flags);
                foreach (var node in configuration.Children)
                    group = AddGradleNode(group, node, 0, icon, flags);
                category = category.Add(group).Parent;
            }
            return category;
        }

        private IProjectTree AddGradleNode(IProjectTree parent, JavaGradleDependencyNode node,
            int depth, ProjectImageMoniker icon, ProjectTreeFlags flags)
        {
            if (depth >= 14) return parent;
            IProjectTree child = NewTree(node.Caption, icon: icon,
                expandedIcon: icon, flags: flags);
            foreach (var transitive in node.Children)
                child = AddGradleNode(child, transitive, depth + 1, icon, flags);
            return parent.Add(child).Parent;
        }

        private void StopGradleLocked()
        {
            ++gradleGeneration;
            if (gradleCancellation != null)
            {
                gradleCancellation.Cancel();
                gradleCancellation.Dispose();
                gradleCancellation = null;
            }
            foreach (var watcher in gradleWatchers) watcher.Dispose();
            gradleWatchers.Clear();
            if (gradleDebounce != null)
            {
                gradleDebounce.Dispose();
                gradleDebounce = null;
            }
        }

        private void DisposeGradleResolution()
        {
            lock (gradleSync)
            {
                gradleDisposed = true;
                StopGradleLocked();
                gradleProjectFile = null;
            }
        }
    }

    internal sealed class JavaGradleDependencyConfiguration
    {
        internal string Name { get; set; }
        internal IList<JavaGradleDependencyNode> Children { get; set; }
    }

    internal sealed class JavaGradleDependencyNode
    {
        internal string Caption { get; set; }
        internal IList<JavaGradleDependencyNode> Children { get; set; }
    }

    internal static class JavaGradleDependencyResolver
    {
        private const int MaxNodes = 2500;
        private const int MaxDepth = 14;
        private const int MaxJsonBytes = 8 * 1024 * 1024;
        private const int TimeoutMilliseconds = 180000;

        internal static IList<JavaGradleDependencyConfiguration> Resolve(
            string projectFile, string command, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            string scriptDir = Path.Combine(Path.GetDirectoryName(
                typeof(JavaGradleDependencyResolver).Assembly.Location),
                "ProjectSystem", "BuildSystem");
            string runner = Path.Combine(scriptDir, "JavaProject.GradleDependencies.ps1");
            string initScript = Path.Combine(scriptDir, "JavaProject.GradleDependencies.init.gradle");
            if (!File.Exists(runner) || !File.Exists(initScript))
                throw new FileNotFoundException("Helper Gradle F4.C.1 mancante nella VSIX");

            string output = Path.Combine(Path.GetTempPath(), "java-cps-gradle-" +
                Guid.NewGuid().ToString("N") + ".json");
            try
            {
                string args = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " +
                    Quote(runner) + " -ProjectDirectory " +
                    Quote(Path.GetDirectoryName(projectFile)) + " -InitScript " +
                    Quote(initScript) + " -OutputFile " + Quote(output);
                if (!string.IsNullOrWhiteSpace(command))
                    args += " -CommandPath " + Quote(command);

                var start = new ProcessStartInfo("powershell.exe", args)
                {
                    WorkingDirectory = Path.GetDirectoryName(projectFile),
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var process = new Process { StartInfo = start })
                {
                    // Drain pipes continuously without retaining user-project output.
                    process.OutputDataReceived += (s, e) => { };
                    process.ErrorDataReceived += (s, e) => { };
                    if (!process.Start()) throw new InvalidOperationException("PowerShell non avviato");
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    var clock = Stopwatch.StartNew();
                    try
                    {
                        while (!process.WaitForExit(200))
                        {
                            cancellation.ThrowIfCancellationRequested();
                            if (clock.ElapsedMilliseconds > TimeoutMilliseconds)
                                throw new TimeoutException("Gradle dependency resolution timeout");
                        }
                        process.WaitForExit(); // Drain asynchronous readers.
                        cancellation.ThrowIfCancellationRequested();
                        if (process.ExitCode != 0)
                            throw new InvalidOperationException("Gradle dependency resolution fallita");
                    }
                    catch
                    {
                        TerminateTree(process);
                        throw;
                    }
                }
                var file = new FileInfo(output);
                if (!file.Exists || file.Length == 0 || file.Length > MaxJsonBytes)
                    throw new InvalidDataException("Snapshot Gradle JSON assente o troppo grande");
                cancellation.ThrowIfCancellationRequested();
                var json = JObject.Parse(File.ReadAllText(output, Encoding.UTF8));
                var groups = json["configurations"] as JArray;
                if (groups == null)
                    throw new InvalidDataException("Schema snapshot Gradle non riconosciuto");
                var result = new List<JavaGradleDependencyConfiguration>();
                int count = 0;
                foreach (JObject group in groups.OfType<JObject>())
                {
                    string name = Clean((string)group["name"]);
                    if (name != "runtimeClasspath" && name != "testRuntimeClasspath" &&
                        name != "compileClasspath" && name != "testCompileClasspath")
                        continue;
                    var dependencies = group["children"] as JArray;
                    if (dependencies == null)
                        throw new InvalidDataException("Elenco configurazione Gradle non valido");
                    result.Add(new JavaGradleDependencyConfiguration
                    {
                        Name = name,
                        Children = ReadChildren(dependencies, 0, ref count)
                    });
                }
                return result;
            }
            finally
            {
                try { if (File.Exists(output)) File.Delete(output); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static IList<JavaGradleDependencyNode> ReadChildren(
            JArray items, int depth, ref int count)
        {
            var nodes = new List<JavaGradleDependencyNode>();
            if (depth >= MaxDepth) return nodes;
            foreach (JObject item in items.OfType<JObject>())
            {
                if (++count > MaxNodes)
                    throw new InvalidDataException("Snapshot Gradle con troppi componenti");
                string group = Clean((string)item["group"]);
                string name = Clean((string)item["name"]);
                string version = Clean((string)item["version"]);
                bool project = (bool?)item["project"] == true;
                bool unresolved = (bool?)item["unresolved"] == true;
                if (string.IsNullOrEmpty(name)) continue;
                string caption = project ? name :
                    (string.IsNullOrEmpty(group) ? name : group + ":" + name) +
                    (string.IsNullOrEmpty(version) ? "" : ":" + version);
                if (unresolved) caption += " (non risolta)";
                if ((bool?)item["cycle"] == true) caption += " (ciclo)";
                var children = item["children"] as JArray;
                nodes.Add(new JavaGradleDependencyNode
                {
                    Caption = caption,
                    Children = children == null ? new List<JavaGradleDependencyNode>() :
                        ReadChildren(children, depth + 1, ref count)
                });
            }
            return nodes;
        }

        private static string Clean(string input)
        {
            return string.IsNullOrEmpty(input) ? string.Empty :
                new string(input.Where(c => !char.IsControl(c)).Take(160).ToArray());
        }

        private static string Quote(string path)
        {
            if (path.IndexOf('"') >= 0 || path.IndexOf('\r') >= 0 || path.IndexOf('\n') >= 0)
                throw new ArgumentException("Percorso non valido");
            return "\"" + path + "\"";
        }

        private static void TerminateTree(Process process)
        {
            try
            {
                if (process.HasExited) return;
                using (var killer = Process.Start(new ProcessStartInfo("taskkill.exe",
                    "/PID " + process.Id + " /T /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }))
                {
                    if (killer != null)
                    {
                        killer.WaitForExit(4000);
                        if (!killer.HasExited) killer.Kill();
                    }
                }
                if (!process.HasExited) process.Kill();
            }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }
    }
}
