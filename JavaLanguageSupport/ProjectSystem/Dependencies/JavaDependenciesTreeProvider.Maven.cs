using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
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
    // F4.B.1: additive CPS implementation. F4.A remains responsible for
    // the root, the Italian captions, JDK, Gradle and external JARs.
    internal partial class JavaDependenciesTreeProvider
    {
        private readonly object mavenSync = new object();
        private CancellationTokenSource mavenCancellation;
        private FileSystemWatcher mavenPomWatcher;
        private Timer mavenDebounce;
        private string mavenProjectFile;
        private string mavenCommand;
        private string mavenFingerprint;
        private string mavenStatus;
        private IList<JavaMavenDependencyNode> mavenNodes = new List<JavaMavenDependencyNode>();
        private int mavenGeneration;
        private bool mavenDisposed;

        private void ScheduleMavenRefresh(string backend,
            IProjectVersionedValue<IProjectCatalogSnapshot> change)
        {
            string projectFile = null;
            string command = null;
            if (string.Equals(backend, "Maven", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var instance = change.Value.Project.ProjectInstance;
                    projectFile = Path.GetFullPath(instance.FullPath);
                    command = instance.GetPropertyValue("JavaMavenCommand");
                }
                catch (Exception) { return; /* Evaluation not ready. */ }
            }

            if (string.IsNullOrEmpty(projectFile) || !File.Exists(projectFile))
            {
                lock (mavenSync)
                {
                    StopMavenLocked();
                    mavenProjectFile = null;
                    mavenFingerprint = null;
                    mavenStatus = null;
                    mavenNodes = new List<JavaMavenDependencyNode>();
                }
                return;
            }

            lock (mavenSync)
            {
                if (mavenDisposed) return;
                if (!string.Equals(mavenProjectFile, projectFile,
                    StringComparison.OrdinalIgnoreCase))
                {
                    StopMavenLocked();
                    mavenProjectFile = projectFile;
                    mavenFingerprint = null;
                    mavenNodes = new List<JavaMavenDependencyNode>();
                    WatchPomLocked(projectFile);
                }
                mavenCommand = command;
            }
            RequestMavenSnapshot();
        }

        private void WatchPomLocked(string projectFile)
        {
            var dir = Path.GetDirectoryName(projectFile);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            try
            {
                mavenDebounce = new Timer(_ => RequestMavenSnapshot(), null,
                    Timeout.Infinite, Timeout.Infinite);
                mavenPomWatcher = new FileSystemWatcher(dir, "pom.xml")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite |
                        NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                FileSystemEventHandler onChanged = (s, e) => DebounceMaven();
                RenamedEventHandler onRenamed = (s, e) => DebounceMaven();
                mavenPomWatcher.Changed += onChanged;
                mavenPomWatcher.Created += onChanged;
                mavenPomWatcher.Deleted += onChanged;
                mavenPomWatcher.Renamed += onRenamed;
            }
            catch (IOException) { /* Project can still refresh on reload. */ }
            catch (UnauthorizedAccessException) { /* Watcher is optional. */ }
        }

        private void DebounceMaven()
        {
            lock (mavenSync)
            {
                if (!mavenDisposed && mavenDebounce != null)
                {
                    try { mavenDebounce.Change(850, Timeout.Infinite); }
                    catch (ObjectDisposedException) { }
                }
            }
        }

        private static string Fingerprint(string projectFile, string command)
        {
            var pom = Path.Combine(Path.GetDirectoryName(projectFile), "pom.xml");
            if (!File.Exists(pom)) return "missing:" + projectFile;
            var info = new FileInfo(pom);
            return projectFile + "|" + info.LastWriteTimeUtc.Ticks + "|" +
                info.Length + "|" + (command ?? "");
        }

        private void RequestMavenSnapshot()
        {
            string projectFile, command, fingerprint;
            CancellationToken token;
            int generation;
            lock (mavenSync)
            {
                if (mavenDisposed || string.IsNullOrEmpty(mavenProjectFile)) return;
                projectFile = mavenProjectFile;
                command = mavenCommand;
                try { fingerprint = Fingerprint(projectFile, command); }
                catch (IOException) { return; }
                catch (UnauthorizedAccessException) { return; }
                if (fingerprint == mavenFingerprint) return;
                mavenFingerprint = fingerprint;
                if (mavenCancellation != null)
                {
                    mavenCancellation.Cancel();
                    mavenCancellation.Dispose();
                }
                mavenCancellation = new CancellationTokenSource();
                token = mavenCancellation.Token;
                generation = ++mavenGeneration;
                mavenStatus = "Risoluzione delle dipendenze Maven in corso...";
                mavenNodes = new List<JavaMavenDependencyNode>();
            }

            PublishMavenTree();
            _ = Task.Run(() =>
            {
                IList<JavaMavenDependencyNode> nodes = null;
                string status = null;
                try
                {
                    nodes = JavaMavenDependencyResolver.Resolve(projectFile, command, token);
                    status = nodes.Count == 0 ? "Nessuna dipendenza Maven" : null;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    // Never show Maven stderr or secrets in the tree or popup.
                    status = "Dipendenze Maven non disponibili (" +
                        ex.GetType().Name + "). Vedere F4.B.1 README.";
                }
                lock (mavenSync)
                {
                    if (mavenDisposed || generation != mavenGeneration ||
                        token.IsCancellationRequested) return;
                    mavenNodes = nodes ?? new List<JavaMavenDependencyNode>();
                    mavenStatus = status;
                }
                PublishMavenTree();
            });
        }

        private void PublishMavenTree()
        {
            // Snapshot creation is carried out on CPS' serialized tree updater.
            // Do not update IProjectTree from the Maven subprocess thread.
            _ = SubmitTreeUpdateAsync((snapshot, exports, token) =>
            {
                token.ThrowIfCancellationRequested();
                lock (mavenSync)
                {
                    if (mavenDisposed || string.IsNullOrEmpty(mavenProjectFile))
                        return Task.FromResult(new TreeUpdateResult(CreateRoot(null)));
                    return Task.FromResult(new TreeUpdateResult(CreateRoot("Maven")));
                }
            });
        }

        private IProjectTree AddResolvedMavenDependencies(IProjectTree category)
        {
            IList<JavaMavenDependencyNode> nodes;
            string status;
            lock (mavenSync)
            {
                nodes = mavenNodes;
                status = mavenStatus;
            }
            var icon = KnownMonikers.Library.ToProjectSystemType();
            var flags = ProjectTreeFlags.Create(ProjectTreeFlags.Common.VirtualFolder);
            if (status != null)
                return category.Add(NewTree(status, icon: icon, expandedIcon: icon,
                    flags: flags)).Parent;
            foreach (var node in nodes)
                category = AddMavenNode(category, node, 0, icon, flags);
            return category;
        }

        private IProjectTree AddMavenNode(IProjectTree parent,
            JavaMavenDependencyNode dependency, int depth,
            ProjectImageMoniker icon, ProjectTreeFlags flags)
        {
            if (depth >= 14) return parent;
            IProjectTree child = NewTree(dependency.Caption, icon: icon,
                expandedIcon: icon, flags: flags);
            foreach (var transit in dependency.Children)
                child = AddMavenNode(child, transit, depth + 1, icon, flags);
            return parent.Add(child).Parent;
        }

        private void StopMavenLocked()
        {
            ++mavenGeneration;
            if (mavenCancellation != null)
            {
                mavenCancellation.Cancel();
                mavenCancellation.Dispose();
                mavenCancellation = null;
            }
            if (mavenPomWatcher != null)
            {
                mavenPomWatcher.Dispose();
                mavenPomWatcher = null;
            }
            if (mavenDebounce != null)
            {
                mavenDebounce.Dispose();
                mavenDebounce = null;
            }
        }

        private void DisposeMavenResolution()
        {
            lock (mavenSync)
            {
                mavenDisposed = true;
                StopMavenLocked();
                mavenProjectFile = null;
            }
        }
    }

    internal sealed class JavaMavenDependencyNode
    {
        internal string Caption { get; set; }
        internal IList<JavaMavenDependencyNode> Children { get; set; }
    }

    internal static class JavaMavenDependencyResolver
    {
        // Guard against malformed JSON, pathological graphs and huge output.
        private const int MaxNodes = 2500;
        private const int MaxDepth = 14;
        private const int MaxJsonBytes = 8 * 1024 * 1024;
        private const int TimeoutMilliseconds = 150000;

        internal static IList<JavaMavenDependencyNode> Resolve(
            string projectFile, string command, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            string script = Path.Combine(Path.GetDirectoryName(
                typeof(JavaMavenDependencyResolver).Assembly.Location),
                "ProjectSystem", "BuildSystem", "JavaProject.MavenDependencies.ps1");
            if (!File.Exists(script))
                throw new FileNotFoundException("JavaProject.MavenDependencies.ps1");
            string output = Path.Combine(Path.GetTempPath(),
                "java-cps-maven-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                string args = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " +
                    Quote(script) + " -ProjectDirectory " +
                    Quote(Path.GetDirectoryName(projectFile)) + " -OutputFile " + Quote(output);
                if (!string.IsNullOrWhiteSpace(command))
                    args += " -CommandPath " + Quote(command);

                var start = new ProcessStartInfo("powershell.exe", args)
                {
                    WorkingDirectory = Path.GetDirectoryName(projectFile),
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                using (var process = new Process { StartInfo = start })
                {
                    process.OutputDataReceived += (s, e) => { /* Deliberately do not retain command output. */ };
                    process.ErrorDataReceived += (s, e) => { /* No unbounded logs in extension memory. */ };
                    if (!process.Start()) throw new InvalidOperationException("PowerShell non avviato");
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    var timer = Stopwatch.StartNew();
                    try
                    {
                        while (!process.WaitForExit(200))
                        {
                            cancellation.ThrowIfCancellationRequested();
                            if (timer.ElapsedMilliseconds > TimeoutMilliseconds)
                                throw new TimeoutException("Maven dependency:tree timeout");
                        }
                        process.WaitForExit(); // Drain async stream readers.
                        cancellation.ThrowIfCancellationRequested();
                        if (process.ExitCode != 0)
                            throw new InvalidOperationException("mvn dependency:tree fallito");
                    }
                    catch
                    {
                        TerminateTree(process);
                        throw;
                    }
                }
                var file = new FileInfo(output);
                if (!file.Exists || file.Length == 0 || file.Length > MaxJsonBytes)
                    throw new InvalidDataException("Output Maven JSON assente o troppo grande");
                cancellation.ThrowIfCancellationRequested();
                JObject root = JObject.Parse(File.ReadAllText(output, Encoding.UTF8));
                var children = root["children"] as JArray;
                if (children == null) return new List<JavaMavenDependencyNode>();
                int count = 0;
                return ReadChildren(children, 0, ref count);
            }
            finally
            {
                try { if (File.Exists(output)) File.Delete(output); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static string Quote(string arg)
        {
            if (arg.IndexOf('"') >= 0 || arg.IndexOf('\n') >= 0 || arg.IndexOf('\r') >= 0)
                throw new ArgumentException("Percorso non valido");
            return "\"" + arg + "\"";
        }

        private static void TerminateTree(Process process)
        {
            try
            {
                if (process.HasExited) return;
                // Windows .NET Framework 4.7 has no Process.Kill(entireProcessTree).
                using (var taskkill = Process.Start(new ProcessStartInfo("taskkill.exe",
                    "/PID " + process.Id + " /T /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }))
                {
                    if (taskkill != null)
                    {
                        taskkill.WaitForExit(4000);
                        if (!taskkill.HasExited) taskkill.Kill();
                    }
                }
                if (!process.HasExited) process.Kill();
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        private static IList<JavaMavenDependencyNode> ReadChildren(
            JArray items, int depth, ref int count)
        {
            var nodes = new List<JavaMavenDependencyNode>();
            if (depth >= MaxDepth) return nodes;
            foreach (var item in items.OfType<JObject>())
            {
                if (++count > MaxNodes)
                    throw new InvalidDataException("Troppi artifact Maven");
                string group = Clean((string)item["groupId"]);
                string artifact = Clean((string)item["artifactId"]);
                string version = Clean((string)item["version"]);
                if (string.IsNullOrEmpty(group) || string.IsNullOrEmpty(artifact))
                    continue;
                string scope = Clean((string)item["scope"]);
                string classifier = Clean((string)item["classifier"]);
                string type = Clean((string)item["type"]);
                string caption = group + ":" + artifact + ":" + version;
                if (!string.IsNullOrEmpty(classifier)) caption += ":" + classifier;
                if (!string.IsNullOrEmpty(type) && type != "jar") caption += " (" + type + ")";
                if (!string.IsNullOrEmpty(scope)) caption += " [" + scope + "]";
                var children = item["children"] as JArray;
                nodes.Add(new JavaMavenDependencyNode
                {
                    Caption = caption,
                    Children = children == null
                        ? new List<JavaMavenDependencyNode>()
                        : ReadChildren(children, depth + 1, ref count)
                });
            }
            return nodes;
        }

        private static string Clean(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return new string(value.Where(c => !char.IsControl(c)).Take(150).ToArray());
        }
    }
}
