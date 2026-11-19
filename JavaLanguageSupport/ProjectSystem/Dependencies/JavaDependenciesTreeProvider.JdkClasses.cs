using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.ProjectSystem;

namespace JavaLanguageSupport.ProjectSystem.Dependencies
{
    // F4.D.1.2: reads the installed JDK runtime image without changing Java/MSBuild/JDT LS.
    // Only top-level .class files are listed. Inner ($) classes and module-info
    // are intentionally excluded from the first iteration to bound CPS tree size.
    internal partial class JavaDependenciesTreeProvider
    {
        private IProjectTree AddJdkClasses(IProjectTree moduleTree,
            JavaJdkSnapshot snapshot, string moduleName)
        {
            if (snapshot.ClassIndex == null) return moduleTree;
            SortedDictionary<string, SortedSet<string>> packages;
            if (!snapshot.ClassIndex.Modules.TryGetValue(moduleName, out packages) ||
                packages.Count == 0) return moduleTree;

            var folderIcon = KnownMonikers.Library.ToProjectSystemType();
            var classIcon = KnownMonikers.Class.ToProjectSystemType();
            var flags = ProjectTreeFlags.Create(ProjectTreeFlags.Common.VirtualFolder);
            int total = packages.Sum(p => p.Value.Count);
            IProjectTree classesFolder = NewTree("Classi (" + total + ")",
                icon: folderIcon, expandedIcon: folderIcon, flags: flags);
            foreach (var package in packages)
            {
                string packageName = string.IsNullOrEmpty(package.Key) ?
                    "(package predefinito)" : package.Key;
                IProjectTree packageFolder = NewTree(packageName, icon: folderIcon,
                    expandedIcon: folderIcon, flags: flags);
                foreach (string className in package.Value)
                {
                    packageFolder = packageFolder.Add(NewTree(className,
                        icon: classIcon, expandedIcon: classIcon, flags: flags)).Parent;
                }
                classesFolder = classesFolder.Add(packageFolder).Parent;
            }
            return moduleTree.Add(classesFolder).Parent;
        }
    }

    internal sealed class JavaJdkClassIndexSnapshot
    {
        internal Dictionary<string, SortedDictionary<string, SortedSet<string>>> Modules
            = new Dictionary<string, SortedDictionary<string, SortedSet<string>>>(
                StringComparer.Ordinal);
    }

    internal static class JavaJdkClassIndex
    {
        private const int TimeoutMilliseconds = 25000;
        private const int MaxInputLines = 120000;
        private const int MaxClasses = 40000;
        private const int MaxPackages = 12000;

        internal static JavaJdkClassIndexSnapshot Read(string home,
            IList<string> expectedModules, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(home))
                throw new InvalidDataException("Directory JDK mancante");
            string jimage = Path.Combine(home, "bin", "jimage.exe");
            string image = Path.Combine(home, "lib", "modules");
            if (!File.Exists(jimage) || !File.Exists(image))
                throw new FileNotFoundException("Strumento jimage o immagine dei moduli non trovato");

            // Only accept known module names returned by java --list-modules.
            var accepted = new HashSet<string>(expectedModules, StringComparer.Ordinal);
            var index = new JavaJdkClassIndexSnapshot();
            string activeModule = null;
            int lines = 0, classes = 0, packages = 0;
            Exception parserFailure = null;
            var sync = new object();
            var start = new ProcessStartInfo(jimage,"list " +Quote(image))
            {
                WorkingDirectory = home,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using (var process = new Process { StartInfo = start })
            {
                process.OutputDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    lock (sync)
                    {
                        if (parserFailure != null) return;
                        try
                        {
                            if (++lines > MaxInputLines)
                                throw new InvalidDataException("Output jimage troppo lungo");
                            string line = e.Data.Trim();
                            if (line.StartsWith("Module: ", StringComparison.Ordinal))
                            {
                                string module = line.Substring("Module: ".Length).Trim();
                                activeModule = accepted.Contains(module) ? module : null;
                                return;
                            }
                            if (activeModule == null || !line.EndsWith(".class",
                                StringComparison.OrdinalIgnoreCase)) return;

                            string relative = line.Substring(0, line.Length - 6);
                            int slash = relative.LastIndexOf('/');
                            string simple = slash < 0 ? relative : relative.Substring(slash + 1);
                            if (simple == "module-info" || simple == "package-info" ||
                                simple.IndexOf('$') >= 0 || simple.Length == 0 ||
                                simple.Length > 160) return;
                            string pkg = slash < 0 ? "" :
                                relative.Substring(0, slash).Replace('/', '.');
                            if (pkg.Length > 320 || pkg.IndexOf('\\') >= 0 ||
                                simple.IndexOf('\\') >= 0 ||
                                pkg.IndexOfAny(new[] { '\r', '\n' }) >= 0) return;

                            SortedDictionary<string, SortedSet<string>> byPackage;
                            if (!index.Modules.TryGetValue(activeModule, out byPackage))
                            {
                                byPackage = new SortedDictionary<string, SortedSet<string>>(
                                    StringComparer.Ordinal);
                                index.Modules.Add(activeModule, byPackage);
                            }
                            SortedSet<string> names;
                            if (!byPackage.TryGetValue(pkg, out names))
                            {
                                if (++packages > MaxPackages)
                                    throw new InvalidDataException("Troppi package JDK");
                                names = new SortedSet<string>(StringComparer.Ordinal);
                                byPackage.Add(pkg, names);
                            }
                            if (names.Add(simple) && ++classes > MaxClasses)
                                throw new InvalidDataException("Troppe classi JDK");
                        }
                        catch (Exception ex)
                        {
                            parserFailure = ex;
                        }
                    }
                };
                process.ErrorDataReceived += (s, e) => { /* Drain only. */ };
                if (!process.Start())
                    throw new InvalidOperationException("jimage.exe non avviato");
                try
                {
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    var timer = Stopwatch.StartNew();
                    while (!process.WaitForExit(150))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (timer.ElapsedMilliseconds > TimeoutMilliseconds)
                            throw new TimeoutException("jimage list timeout");
                    }
                    process.WaitForExit(); // Flush DataReceived callbacks.
                    cancellation.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0)
                        throw new InvalidOperationException("jimage list fallito");
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
            if (parserFailure != null) throw parserFailure;
            if (classes == 0) throw new InvalidDataException("Nessuna classe indicizzata");
            return index;
        }

        private static string Quote(string path)
        {
            if (path.IndexOf('"') >= 0 || path.IndexOf('\r') >= 0 ||
                path.IndexOf('\n') >= 0)
                throw new ArgumentException("Percorso jimage non valido");
            return "\"" + path + "\"";
        }
    }
}
