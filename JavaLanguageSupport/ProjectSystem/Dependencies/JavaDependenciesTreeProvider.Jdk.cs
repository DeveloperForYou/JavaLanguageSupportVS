using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.Properties;

namespace JavaLanguageSupport.ProjectSystem.Dependencies
{
    // Read-only JDK snapshot for the existing CPS tree. Does not change MSBuild,
    // Maven, Gradle, JDT LS or the user's project configuration.
    internal partial class JavaDependenciesTreeProvider
    {
        private readonly object jdkSync = new object();
        private CancellationTokenSource jdkCancellation;
        private string jdkFingerprint;
        private string jdkBackend;
        private string jdkStatus;
        private JavaJdkSnapshot jdkSnapshot;
        private int jdkGeneration;
        private bool jdkDisposed;

        private void ScheduleJdkRefresh(string backend,
            IProjectVersionedValue<IProjectCatalogSnapshot> change)
        {
            string projectFile;
            string requestedHome;
            try
            {
                var instance = change.Value.Project.ProjectInstance;
                projectFile = Path.GetFullPath(instance.FullPath);
                // Used by Plain build and JDT LS; Maven/Gradle retain their own toolchains.
                requestedHome = instance.GetPropertyValue("JavaJdkHome");
            }
            catch (Exception)
            {
                // Evaluation may be unavailable while CPS is loading or unloading.
                return;
            }

            if (string.IsNullOrEmpty(projectFile) || !File.Exists(projectFile)) return;
            string environmentHome = Environment.GetEnvironmentVariable("JAVA_HOME") ?? "";
            string environmentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            string fingerprint = projectFile + "\n" + (requestedHome ?? "") +
                "\n" + environmentHome + "\n" + environmentPath;
            int generation;
            CancellationToken token;
            lock (jdkSync)
            {
                if (jdkDisposed) return;
                jdkBackend = backend;
                if (string.Equals(jdkFingerprint, fingerprint, StringComparison.Ordinal))
                    return;
                jdkFingerprint = fingerprint;
                StopJdkLocked();
                jdkCancellation = new CancellationTokenSource();
                token = jdkCancellation.Token;
                generation = jdkGeneration;
                jdkSnapshot = null;
                // F4D13-JDK-SOURCE-RESET
                ClearJdkSourceRegistration();
                jdkStatus = "Rilevamento del JDK in corso...";
            }

            PublishJdkTree();
            _ = Task.Run(() =>
            {
                JavaJdkSnapshot result = null;
                string status = null;
                try
                {
                    result = JavaJdkDiscovery.Resolve(requestedHome, environmentHome,
                        environmentPath, token);
                    // F4D12-JDK-CLASSES
                    if (result.Modules.Count != 0)
                    {
                        try
                        {
                            result.ClassIndex = JavaJdkClassIndex.Read(result.Home,
                                result.Modules, token);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            // Class discovery is optional: preserve JDK version/modules.
                            result.ClassIndexStatus = "Indice classi non disponibile (" +
                                ex.GetType().Name + ").";
                        }
                    }
                    status = result.Modules.Count == 0 ?
                        "Questo JDK non espone moduli JPMS (Java 8)." : null;
                }
                catch (OperationCanceledException) { return; }
                catch (FileNotFoundException)
                {
                    status = "JDK non trovato (JavaJdkHome, JAVA_HOME o PATH).";
                }
                catch (Exception ex)
                {
                    // Never show paths, full subprocess output or potentially
                    // sensitive configuration values in Solution Explorer.
                    status = "Impossibile leggere i moduli del JDK (" +
                        ex.GetType().Name + ").";
                }
                lock (jdkSync)
                {
                    if (jdkDisposed || generation != jdkGeneration ||
                        token.IsCancellationRequested) return;
                    jdkSnapshot = result;
                    jdkStatus = status;
                    // F4D13-JDK-SOURCE-RESULT
                    RememberJdkSourceRegistration(projectFile,
                        result == null ? null : result.Home);
                }
                dependencyLanguageSync.JdkResolved(projectFile, requestedHome, result == null ? null : result.Home);
                PublishJdkTree();
            });
        }

        private void PublishJdkTree()
        {
            _ = SubmitTreeUpdateAsync((snapshot, exports, token) =>
            {
                token.ThrowIfCancellationRequested();
                string backend;
                lock (jdkSync)
                {
                    if (jdkDisposed) return Task.FromResult(
                        new TreeUpdateResult(CreateRoot(null)));
                    backend = jdkBackend;
                }
                return Task.FromResult(new TreeUpdateResult(CreateRoot(backend)));
            });
        }

        private string GetJdkCaption()
        {
            lock (jdkSync)
            {
                return jdkSnapshot == null || string.IsNullOrEmpty(jdkSnapshot.MajorVersion)
                    ? "JDK" : "JDK " + jdkSnapshot.MajorVersion;
            }
        }

        private IProjectTree AddResolvedJdkModules(IProjectTree category)
        {
            JavaJdkSnapshot current;
            string status;
            lock (jdkSync)
            {
                current = jdkSnapshot;
                status = jdkStatus;
            }
            var icon = KnownMonikers.Library.ToProjectSystemType();
            var flags = ProjectTreeFlags.Create(ProjectTreeFlags.Common.VirtualFolder);
            if (current != null && !string.IsNullOrWhiteSpace(current.Version))
            {
                category = category.Add(NewTree("Versione: " + current.Version,
                    icon: icon, expandedIcon: icon, flags: flags)).Parent;
            }
            if (!string.IsNullOrEmpty(status))
                return category.Add(NewTree(status, icon: icon, expandedIcon: icon,
                    flags: flags)).Parent;
            if (current == null) return category;
            if (!string.IsNullOrEmpty(current.ClassIndexStatus))
                category = category.Add(NewTree(current.ClassIndexStatus,
                    icon: icon, expandedIcon: icon, flags: flags)).Parent;
            foreach (string module in current.Modules)
            {
                IProjectTree moduleTree = NewTree(module, icon: icon,
                    expandedIcon: icon, flags: flags);
                moduleTree = AddJdkClasses(moduleTree, current, module);
                category = category.Add(moduleTree).Parent;
            }
            return category;
        }

        private void StopJdkLocked()
        {
            ++jdkGeneration;
            if (jdkCancellation != null)
            {
                jdkCancellation.Cancel();
                jdkCancellation.Dispose();
                jdkCancellation = null;
            }
        }

        private void DisposeJdkResolution()
        {
            lock (jdkSync)
            {
                jdkDisposed = true;
                StopJdkLocked();
                // F4D13-JDK-SOURCE-DISPOSE
                ClearJdkSourceRegistration();
                jdkSnapshot = null;
            }
        }
    }

    internal sealed class JavaJdkSnapshot
    {
        internal string Version { get; set; }
        internal string MajorVersion { get; set; }
        internal IList<string> Modules { get; set; }
        internal string Home { get; set; }
        internal JavaJdkClassIndexSnapshot ClassIndex { get; set; }
        internal string ClassIndexStatus { get; set; }
    }

    internal static class JavaJdkDiscovery
    {
        private const int TimeoutMilliseconds = 15000;
        private const int MaxModules = 512;
        private const int MaxOutputLines = 1024;

        internal static JavaJdkSnapshot Resolve(string configuredHome, string javaHome,
            string environmentPath, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            string home = FindJdkHome(configuredHome, javaHome, environmentPath, cancellation);
            string java = Path.Combine(home, "bin", "java.exe");
            string version = ReadReleaseVersion(home);

            // JDK 8 predates JPMS. Do not treat absence of --list-modules as an error.
            if (version.StartsWith("1.8", StringComparison.Ordinal))
                return new JavaJdkSnapshot {
                    Home = home, Version = version, MajorVersion = "8", Modules = new List<string>()
                };

            var output = new List<string>();
            var start = new ProcessStartInfo(java, "--list-modules")
            {
                WorkingDirectory = home,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var process = new Process { StartInfo = start })
            {
                process.OutputDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    lock (output)
                    {
                        if (output.Count < MaxOutputLines) output.Add(e.Data);
                    }
                };
                process.ErrorDataReceived += (s, e) => { /* No stderr retained. */ };
                cancellation.ThrowIfCancellationRequested();
                if (!process.Start())
                    throw new InvalidOperationException("java.exe non avviato");
                try
                {
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    var stopwatch = Stopwatch.StartNew();
                    while (!process.WaitForExit(150))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (stopwatch.ElapsedMilliseconds >= TimeoutMilliseconds)
                            throw new TimeoutException("java --list-modules non risponde");
                    }
                    process.WaitForExit(); // Flush async output callbacks.
                    cancellation.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0)
                        throw new InvalidOperationException("java --list-modules fallito");
                }
                catch
                {
                    if (!process.HasExited)
                    {
                        try { process.Kill(); }
                        catch (InvalidOperationException) { }
                        catch (Win32Exception) { }
                    }
                    throw;
                }
            }

            var modules = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (output)
            {
                if (output.Count >= MaxOutputLines)
                    throw new InvalidDataException("Elenco dei moduli JDK troppo grande");
                foreach (string raw in output)
                {
                    string text = (raw ?? "").Trim();
                    int at = text.IndexOf('@');
                    string module = at < 0 ? text : text.Substring(0, at);
                    if (!Regex.IsMatch(module, @"^[A-Za-z][A-Za-z0-9_.]*$"))
                        continue;
                    if (module == "java.base" && string.IsNullOrEmpty(version) && at > 0)
                        version = CleanVersion(text.Substring(at + 1));
                    modules.Add(module);
                    if (modules.Count > MaxModules)
                        throw new InvalidDataException("Troppi moduli JDK");
                }
            }
            if (modules.Count == 0)
                throw new InvalidDataException("Nessun modulo restituito da java.exe");
            string major = GetMajorVersion(version);
            return new JavaJdkSnapshot
            {
                Version = version,
                Home = home,
                MajorVersion = major,
                Modules = modules.ToList()
            };
        }

        private static string FindJdkHome(string configuredHome, string javaHome,
            string environmentPath, CancellationToken cancellation)
        {
            // F4D1.1-JAVA-PATH-SHIM: discover java.home through launchers.
            if (!string.IsNullOrWhiteSpace(configuredHome))
            {
                // An explicit project override is authoritative: never silently
                // substitute a different JDK when it is invalid.
                string explicitHome = NormalizeHome(configuredHome);
                if (IsJdk(explicitHome)) return explicitHome;
                throw new FileNotFoundException("JavaJdkHome non valido");
            }

            if (!string.IsNullOrWhiteSpace(javaHome))
            {
                try
                {
                    string defaultHome = NormalizeHome(javaHome);
                    if (IsJdk(defaultHome)) return defaultHome;
                }
                catch (Exception ex) when (ex is IOException ||
                    ex is ArgumentException || ex is NotSupportedException ||
                    ex is UnauthorizedAccessException)
                { /* Invalid JAVA_HOME may be stale; try the process PATH. */ }
            }

            // Scan PATH in launch order, just like Windows resolves 'java'.
            // A direct JDK/bin is preferred without starting any process.
            string[] parts = (environmentPath ?? "").Split(';');
            foreach (string part in parts)
            {
                cancellation.ThrowIfCancellationRequested();
                string candidate = part.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                try
                {
                    string directory = Path.GetFullPath(candidate);
                    if (!Directory.Exists(directory)) continue;
                    if (string.Equals(Path.GetFileName(directory.TrimEnd('\\', '/')),
                        "bin", StringComparison.OrdinalIgnoreCase))
                    {
                        var parent = Directory.GetParent(directory);
                        if (parent != null && IsJdk(parent.FullName)) return parent.FullName;
                    }
                    string launcher = Path.Combine(directory, "java.exe");
                    if (!File.Exists(launcher)) continue;
                    string runtimeHome = ReadJavaHomeFromExecutable(launcher, cancellation);
                    if (IsJdk(runtimeHome)) return runtimeHome;
                    // JDK 8 reports an embedded ...\\jre home.
                    if (!string.IsNullOrWhiteSpace(runtimeHome) &&
                        string.Equals(Path.GetFileName(runtimeHome.TrimEnd('\\', '/')),
                            "jre", StringComparison.OrdinalIgnoreCase))
                    {
                        var parent = Directory.GetParent(runtimeHome);
                        if (parent != null && IsJdk(parent.FullName)) return parent.FullName;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException ||
                    ex is ArgumentException || ex is NotSupportedException ||
                    ex is UnauthorizedAccessException || ex is Win32Exception)
                { /* Continue to the next PATH entry. */ }
            }
            throw new FileNotFoundException("Nessun JDK rilevato");
        }

        // Only the JVM-provided 'java.home' is read. No shell, registry scan,
        // project execution, persistent output or network request is needed.
        private static string ReadJavaHomeFromExecutable(string launcher,
            CancellationToken cancellation)
        {
            var lines = new List<string>();
            var start = new ProcessStartInfo(launcher,
                "-XshowSettings:properties -version")
            {
                WorkingDirectory = Path.GetDirectoryName(launcher),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            using (var process = new Process { StartInfo = start })
            {
                DataReceivedEventHandler collect = (s, e) =>
                {
                    if (e.Data == null) return;
                    lock (lines)
                    {
                        if (lines.Count < 512) lines.Add(e.Data);
                    }
                };
                process.OutputDataReceived += collect;
                process.ErrorDataReceived += collect;
                cancellation.ThrowIfCancellationRequested();
                if (!process.Start()) return null;
                try
                {
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    var watch = Stopwatch.StartNew();
                    while (!process.WaitForExit(150))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (watch.ElapsedMilliseconds > 5000)
                            throw new TimeoutException("java.home lookup timeout");
                    }
                    process.WaitForExit();
                    cancellation.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0) return null;
                }
                catch
                {
                    if (!process.HasExited)
                    {
                        try { process.Kill(); }
                        catch (InvalidOperationException) { }
                        catch (Win32Exception) { }
                    }
                    throw;
                }
            }
            lock (lines)
            {
                foreach (string line in lines)
                {
                    var match = Regex.Match(line ?? string.Empty,
                        @"^\s*java\.home\s*=\s*(.+?)\s*$", RegexOptions.IgnoreCase);
                    if (!match.Success) continue;
                    string value = match.Groups[1].Value.Trim().Trim('"');
                    try { return NormalizeHome(value); }
                    catch (Exception ex) when (ex is IOException ||
                        ex is ArgumentException || ex is NotSupportedException ||
                        ex is UnauthorizedAccessException)
                    { return null; }
                }
            }
            return null;
        }

        private static string NormalizeHome(string path)
        {
            return Path.GetFullPath(path.Trim().Trim('"'));
        }

        private static bool IsJdk(string home)
        {
            return !string.IsNullOrWhiteSpace(home) &&
                File.Exists(Path.Combine(home, "bin", "java.exe")) &&
                File.Exists(Path.Combine(home, "bin", "javac.exe"));
        }

        private static string ReadReleaseVersion(string home)
        {
            string release = Path.Combine(home, "release");
            try
            {
                if (!File.Exists(release) || new FileInfo(release).Length > 65536)
                    return "";
                foreach (string line in File.ReadLines(release).Take(128))
                {
                    if (line.StartsWith("JAVA_VERSION=", StringComparison.Ordinal))
                        return CleanVersion(line.Substring("JAVA_VERSION=".Length).Trim('"'));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return "";
        }

        private static string CleanVersion(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";
            var value = new string(input.Where(c => char.IsLetterOrDigit(c) ||
                c == '.' || c == '_' || c == '-' || c == '+').Take(48).ToArray());
            return value;
        }

        private static string GetMajorVersion(string version)
        {
            if (string.IsNullOrEmpty(version)) return "";
            string[] parts = version.Split('.');
            string candidate = parts[0] == "1" && parts.Length > 1 ? parts[1] : parts[0];
            var match = Regex.Match(candidate, @"^\d+");
            return match.Success ? match.Value : "";
        }
    }
}
