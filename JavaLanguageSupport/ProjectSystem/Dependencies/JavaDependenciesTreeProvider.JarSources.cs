using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace JavaLanguageSupport.ProjectSystem.Dependencies
{
    // F4.D.2.1: virtual JAR class nodes only. Never intercept ordinary .java or JDK nodes.
    internal static class JavaExternalJarSourceTreeFlags
    {
        internal const string ClassNodeName = "JavaExternalJarSourceClass";
        internal static readonly ProjectTreeFlags ClassNode =
            ProjectTreeFlags.Create(ClassNodeName);
    }

    // Store only the exact set of JARs already resolved by the active Plain project's
    // CPS snapshot. A node's caption is NOT interpreted as a filesystem path.
    internal static class JavaExternalJarSourceRegistry
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, Dictionary<string, string>> Projects =
            new Dictionary<string, Dictionary<string, string>>(
                StringComparer.OrdinalIgnoreCase);

        internal static void Replace(string projectFile, IList<JavaExternalJarSnapshot> jars)
        {
            if (string.IsNullOrWhiteSpace(projectFile)) return;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);
            var counts = jars.GroupBy(j => Path.GetFileName(j.Path),
                StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key,
                    g => g.Count(), StringComparer.OrdinalIgnoreCase);
            foreach (var jar in jars)
            {
                if (!string.IsNullOrEmpty(jar.Status) ||
                    string.IsNullOrWhiteSpace(jar.Path)) continue;
                string caption = Path.GetFileName(jar.Path);
                if (counts[caption] > 1)
                    caption += " (" + Path.GetFileName(Path.GetDirectoryName(jar.Path)) + ")";
                if (map.ContainsKey(caption)) ambiguous.Add(caption);
                else map.Add(caption, jar.Path);
            }
            foreach (string caption in ambiguous) map.Remove(caption);
            lock (Sync) Projects[projectFile] = map;
        }

        internal static void Clear(string projectFile)
        {
            if (string.IsNullOrWhiteSpace(projectFile)) return;
            lock (Sync) Projects.Remove(projectFile);
        }

        internal static bool TryGet(string projectFile, string caption, out string path)
        {
            path = null;
            lock (Sync)
            {
                Dictionary<string, string> map;
                return Projects.TryGetValue(projectFile, out map) &&
                    map.TryGetValue(caption, out path);
            }
        }
    }

    // F4D22-JAR-NAV-DIAGNOSTICS: bounded, non-sensitive diagnostics only.
    internal static class JavaExternalJarSourceTrace
    {
        private static readonly object Sync = new object();
        internal static readonly string FileName = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JavaLanguageSupportVS", "logs", "jar-source-navigation.log");

        // F4D23-CPS-TRACE-ROUTING: provisional MEF / routing diagnostics.
        private static readonly HashSet<string> WrittenOnce =
            new HashSet<string>(StringComparer.Ordinal);

        internal static void WriteOnce(string stage)
        {
            lock (Sync)
            {
                if (!WrittenOnce.Add(stage)) return;
            }
            Write(stage);
        }

        internal static bool IsCandidate(IImmutableSet<IProjectTree> nodes)
        {
            if (nodes == null || nodes.Count != 1) return false;
            IProjectTree node = nodes.First();
            if (node.Flags.Contains(JavaExternalJarSourceTreeFlags.ClassNodeName) ||
                string.Equals(node.Caption, "Greeting", StringComparison.Ordinal))
                return true;
            IProjectTree ancestor = node.Parent;
            for (int depth = 0; ancestor != null && depth < 6;
                ancestor = ancestor.Parent, depth++)
            {
                if (string.Equals(ancestor.Caption, "JAR Esterni", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        // Only node captions; no filesystem paths, project paths or JAR contents.
        internal static string Describe(IImmutableSet<IProjectTree> nodes)
        {
            if (nodes == null) return "nodes=null";
            if (nodes.Count != 1) return "nodes=" + nodes.Count;
            IProjectTree node = nodes.First();
            var parts = new List<string>();
            for (int depth = 0; node != null && depth < 7; depth++, node = node.Parent)
            {
                string caption = (node.Caption ?? "").Replace('\r', ' ').Replace('\n', ' ');
                if (caption.Length > 90) caption = caption.Substring(0, 90);
                parts.Add(caption);
            }
            return "chain=" + string.Join(" <- ", parts) + "; marked=" +
                nodes.First().Flags.Contains(JavaExternalJarSourceTreeFlags.ClassNodeName);
        }

        internal static void Write(string stage)
        {
            try
            {
                lock (Sync)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FileName));
                    if (File.Exists(FileName) && new FileInfo(FileName).Length > 1024 * 1024)
                        File.Delete(FileName);
                    File.AppendAllText(FileName, DateTime.UtcNow.ToString("o") + " " +
                        stage + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch (Exception) { /* Diagnostics must never break CPS. */ }
        }
    }

    internal sealed class JavaExternalJarSourceLocator
    {
        internal string JarCaption;
        internal string Package;
        internal string Class;

        private static bool IsJavaIdentifier(string input)
        {
            if (string.IsNullOrWhiteSpace(input) || input.Length > 160 ||
                !((input[0] >= 'A' && input[0] <= 'Z') ||
                  (input[0] >= 'a' && input[0] <= 'z') || input[0] == '_'))
                return false;
            return input.All(c => (c >= 'A' && c <= 'Z') ||
                (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_');
        }

        internal static bool TryFrom(IImmutableSet<IProjectTree> nodes,
            out JavaExternalJarSourceLocator location)
        {
            location = null;
            if (nodes == null || nodes.Count != 1) return false;
            IProjectTree leaf = nodes.First();
            // CPS may project away custom flags. Confirm the full JAR
            // hierarchy and resolve the library from the registered CPS items.
            IProjectTree package = leaf.Parent;
            IProjectTree classes = package == null ? null : package.Parent;
            IProjectTree jar = classes == null ? null : classes.Parent;
            IProjectTree category = jar == null ? null : jar.Parent;
            if (category == null || category.Caption != "JAR Esterni" ||
                package == null || classes == null || jar == null ||
                !classes.Caption.StartsWith("Classi (", StringComparison.Ordinal) ||
                !IsJavaIdentifier(leaf.Caption) || string.IsNullOrWhiteSpace(jar.Caption) ||
                jar.Caption.IndexOf(".jar", StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            string pkg = package.Caption == "(package predefinito)" ? "" : package.Caption;
            if (pkg.Length > 320 || (pkg.Length > 0 &&
                !pkg.Split('.').All(IsJavaIdentifier))) return false;
            location = new JavaExternalJarSourceLocator
            {
                JarCaption = jar.Caption, Package = pkg, Class = leaf.Caption
            };
            return true;
        }
    }

    internal static class JavaExternalJarSourceArchive
    {
        private const long MaxArchiveBytes = 256L * 1024 * 1024;
        private const long MaxSourceBytes = 4L * 1024 * 1024;
        private const int MaxDisassemblyChars = 1024 * 1024;

        internal static string OpenOne(string jar, JavaExternalJarSourceLocator location,
            string jdkHome)
        {
            if (string.IsNullOrWhiteSpace(jar) || !File.Exists(jar) ||
                !jar.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                throw new FileNotFoundException("La libreria JAR non e' piu' disponibile.");

            string dotted = string.IsNullOrEmpty(location.Package) ? location.Class :
                location.Package + "." + location.Class;
            string entryName = (string.IsNullOrEmpty(location.Package) ? "" :
                location.Package.Replace('.', '/') + "/") + location.Class + ".java";
            string sidecar = jar.Substring(0, jar.Length - 4) + "-sources.jar";

            // The source JAR, when present, has priority. Some libraries put .java
            // files directly in their binary JAR; support that form as well.
            foreach (string archivePath in new[] { sidecar, jar }.Distinct(
                StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(archivePath)) continue;
                var metadata = new FileInfo(archivePath);
                if (metadata.Length <= 0 || metadata.Length > MaxArchiveBytes) continue;
                using (var stream = new FileStream(archivePath, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    ZipArchiveEntry entry = zip.GetEntry(entryName);
                    if (entry == null) continue;
                    if (entry.Length <= 0 || entry.Length > MaxSourceBytes)
                        throw new InvalidDataException("Dimensione del sorgente non valida.");
                    string file = GetCacheFile(jar, archivePath, location, ".java");
                    if (File.Exists(file)) return file;
                    using (var source = entry.Open())
                    using (var buffer = new MemoryStream())
                    {
                        byte[] chunk = new byte[16384];
                        long count = 0;
                        int read;
                        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
                        {
                            count += read;
                            if (count > MaxSourceBytes)
                                throw new InvalidDataException("Sorgente troppo grande.");
                            buffer.Write(chunk, 0, read);
                        }
                        WriteOnce(file, buffer.ToArray());
                        return file;
                    }
                }
            }

            // With no source available, show declarations/bytecode rather than
            // opening a blank editor or pretending that decompilation is source.
            string fallback = GetCacheFile(jar, jar, location, ".txt");
            if (!File.Exists(fallback))
            {
                string disassembly = RunJavap(jar, dotted, jdkHome);
                string header = "Classe: " + dotted + Environment.NewLine +
                    "Sorgente Java non disponibile: nessun -sources.jar o .java incorporato." +
                    Environment.NewLine + "Visualizzazione javap -c -p (bytecode, NON sorgente Java)." +
                    Environment.NewLine + Environment.NewLine;
                WriteOnce(fallback, new UTF8Encoding(false).GetBytes(header + disassembly));
            }
            return fallback;
        }

        private static string RunJavap(string jar, string dotted, string jdkHome)
        {
            string binary = string.IsNullOrWhiteSpace(jdkHome) ? null :
                Path.Combine(jdkHome, "bin", "javap.exe");
            if (string.IsNullOrEmpty(binary) || !File.Exists(binary))
                binary = "javap.exe"; // Explicit user action; PATH lookup only.
            var output = new StringBuilder();
            var guard = new object();
            bool truncated = false;
            var psi = new ProcessStartInfo(binary,
                "-classpath " + Quote(jar) + " -c -p " + dotted)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using (var process = new Process { StartInfo = psi })
            {
                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data == null) return;
                    lock (guard)
                    {
                        if (output.Length + e.Data.Length + 2 > MaxDisassemblyChars)
                            truncated = true;
                        else output.AppendLine(e.Data);
                    }
                };
                process.ErrorDataReceived += (sender, e) => { /* Drain errors, no output leaks. */ };
                if (!process.Start())
                    throw new InvalidOperationException("javap non avviato.");
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                var clock = Stopwatch.StartNew();
                try
                {
                    while (!process.WaitForExit(150))
                    {
                        if (clock.ElapsedMilliseconds > 20000)
                            throw new TimeoutException("javap timeout.");
                    }
                    process.WaitForExit();
                    if (process.ExitCode != 0 || truncated || output.Length == 0)
                        throw new InvalidDataException("javap non ha restituito dati validi.");
                }
                catch
                {
                    try { if (!process.HasExited) process.Kill(); }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception) { }
                    throw;
                }
            }
            return output.ToString();
        }

        private static string Quote(string path)
        {
            if (path.IndexOf('"') >= 0 || path.IndexOf('\r') >= 0 ||
                path.IndexOf('\n') >= 0)
                throw new ArgumentException("Percorso JAR non valido.");
            return "\"" + path + "\"";
        }

        private static string GetCacheFile(string jar, string archive,
            JavaExternalJarSourceLocator location, string extension)
        {
            FileInfo info = new FileInfo(jar);
            FileInfo sourceInfo = new FileInfo(archive);
            string signature = jar.ToUpperInvariant() + "|" + info.Length + "|" +
                info.LastWriteTimeUtc.Ticks + "|" + archive.ToUpperInvariant() + "|" +
                sourceInfo.Length + "|" + sourceInfo.LastWriteTimeUtc.Ticks;
            string digest;
            using (var sha = SHA256.Create())
                digest = BitConverter.ToString(sha.ComputeHash(
                    Encoding.UTF8.GetBytes(signature)), 0, 12).Replace("-", "");
            string root = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
                "JavaLanguageSupportVS", "jar-sources", digest);
            string package = location.Package.Replace('.', Path.DirectorySeparatorChar);
            string file = Path.GetFullPath(Path.Combine(root, package,
                location.Class + extension));
            string absoluteRoot = Path.GetFullPath(root).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!file.StartsWith(absoluteRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Percorso cache JAR non valido.");
            return file;
        }

        private static void WriteOnce(string path, byte[] bytes)
        {
            if (File.Exists(path)) return;
            string dir = Path.GetDirectoryName(path);
            // The selected package/class names have already been validated. Refuse
            // redirection inside our own cache instead of following junctions.
            var stack = new Stack<string>();
            for (string current = dir; !string.IsNullOrEmpty(current);
                current = Path.GetDirectoryName(current))
            {
                stack.Push(current);
                if (string.Equals(Path.GetFileName(current), "jar-sources",
                    StringComparison.OrdinalIgnoreCase)) break;
            }
            while (stack.Count > 0)
            {
                string part = stack.Pop();
                if (Directory.Exists(part))
                {
                    if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Directory cache JAR reindirizzata.");
                }
                else Directory.CreateDirectory(part);
            }
            string pending = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(pending, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None)) stream.Write(bytes, 0, bytes.Length);
                try { File.Move(pending, path); }
                catch (IOException) { if (!File.Exists(path)) throw; }
                if (File.Exists(path)) File.SetAttributes(path,
                    File.GetAttributes(path) | FileAttributes.ReadOnly);
            }
            finally
            {
                if (File.Exists(pending)) File.Delete(pending);
            }
        }
    }

    internal abstract class JavaExternalJarSourceOpenCommandBase : IAsyncCommandGroupHandler
    {
        private readonly long[] commandIds;
        private readonly UnconfiguredProject project;
        private readonly IServiceProvider services;

        protected JavaExternalJarSourceOpenCommandBase(UnconfiguredProject project,
            IServiceProvider services, params long[] commandIds)
        {
            this.project = project;
            this.services = services;
            this.commandIds = commandIds;
            JavaExternalJarSourceTrace.WriteOnce("mef: " + GetType().Name);
        }

        private bool Matches(long id, IImmutableSet<IProjectTree> nodes,
            out JavaExternalJarSourceLocator locator)
        {
            locator = null;
            return commandIds.Contains(id) &&
                JavaExternalJarSourceLocator.TryFrom(nodes, out locator);
        }

        public Task<CommandStatusResult> GetCommandStatusAsync(
            IImmutableSet<IProjectTree> nodes, long commandId, bool focused,
            string commandText, CommandStatus progressiveStatus)
        {
            JavaExternalJarSourceLocator ignored;
            if (JavaExternalJarSourceTrace.IsCandidate(nodes))
                JavaExternalJarSourceTrace.WriteOnce("status: " + GetType().Name +
                    " cmd=" + commandId + "; " +
                    JavaExternalJarSourceTrace.Describe(nodes));
            if (!Matches(commandId, nodes, out ignored))
            {
                // Do not log every ordinary Solution Explorer status query.
                if (nodes != null && nodes.Count == 1 &&
                    nodes.First().Flags.Contains(JavaExternalJarSourceTreeFlags.ClassNodeName))
                    JavaExternalJarSourceTrace.Write("status: nodo JAR marcato ma route non riconosciuta");
                return Task.FromResult(CommandStatusResult.Unhandled);
            }
            JavaExternalJarSourceTrace.Write("status: comando di apertura supportato");
            return Task.FromResult(new CommandStatusResult(true, commandText,
                progressiveStatus | CommandStatus.Enabled | CommandStatus.Supported));
        }

        public Task<bool> TryHandleCommandAsync(
            IImmutableSet<IProjectTree> nodes, long commandId, bool focused,
            long commandExecuteOptions, IntPtr variantArgIn, IntPtr variantArgOut)
        {
            JavaExternalJarSourceLocator locator;
            if (JavaExternalJarSourceTrace.IsCandidate(nodes))
                JavaExternalJarSourceTrace.WriteOnce("exec: " + GetType().Name +
                    " cmd=" + commandId + "; " +
                    JavaExternalJarSourceTrace.Describe(nodes));
            if (!Matches(commandId, nodes, out locator)) return Task.FromResult(false);
            JavaExternalJarSourceTrace.Write("azione: " + locator.Class + " (" + locator.JarCaption + ")");

            // CPS may synchronously wait on command handlers; return immediately.
            // ZIP extraction and javap run only on explicit activation, off UI thread.
            string projectFile = project.FullPath;
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    string file = await Task.Run(() =>
                    {
                        string jar;
                        if (!JavaExternalJarSourceRegistry.TryGet(projectFile,
                            locator.JarCaption, out jar))
                        {
                            JavaExternalJarSourceTrace.Write("risoluzione: JAR assente nel registro CPS");
                            throw new FileNotFoundException(
                                "Riferimento JAR non disponibile o ambiguo.");
                        }
                        JavaExternalJarSourceTrace.Write("risoluzione: JAR trovato, archivio sorgenti=" +
                            File.Exists(jar.Substring(0, jar.Length - 4) + "-sources.jar"));
                        string home;
                        JavaJdkSourceRegistry.TryGet(projectFile, out home);
                        string source = JavaExternalJarSourceArchive.OpenOne(jar, locator, home);
                        JavaExternalJarSourceTrace.Write("estrazione: completata (" +
                            Path.GetExtension(source) + ")");
                        return source;
                    });
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    VsShellUtilities.OpenDocument(services, file);
                    JavaExternalJarSourceTrace.Write("editor: apertura richiesta completata");
                }
                catch (Exception ex)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    JavaExternalJarSourceTrace.Write("errore: " + ex.GetType().Name);
                    string message = ex is FileNotFoundException ?
                        "JAR/sorgente non disponibile o riferimento ambiguo." :
                        "Impossibile aprire la classe JAR (" +
                        ex.GetType().Name + ").";
                    var bar = services.GetService(typeof(SVsStatusbar)) as IVsStatusbar;
                    if (bar != null) bar.SetText("Java: " + message);
                    VsShellUtilities.ShowMessageBox(services, message +
                        Environment.NewLine + "Diagnostica: %LOCALAPPDATA%\\JavaLanguageSupportVS\\logs\\jar-source-navigation.log",
                        "Java - JAR Esterni",
                        OLEMSGICON.OLEMSGICON_WARNING,
                        OLEMSGBUTTON.OLEMSGBUTTON_OK,
                        OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                }
            });
            return Task.FromResult(true);
        }
    }

    [ExportCommandGroup("{60481700-078B-11D1-AAF8-00A0C9055A90}")]
    [AppliesTo("JavaLanguageSupportCps")]
    [Order(1000)]
    internal sealed class JavaExternalJarSourceActivateCommand :
        JavaExternalJarSourceOpenCommandBase
    {
        [ImportingConstructor]
        public JavaExternalJarSourceActivateCommand(UnconfiguredProject project,
            [Import(typeof(SVsServiceProvider))] IServiceProvider services)
            : base(project, services, 2L, 3L) { }
    }

    [ExportCommandGroup("{5EFC7975-14BC-11CF-9B2B-00AA00573819}")]
    [AppliesTo("JavaLanguageSupportCps")]
    [Order(1000)]
    internal sealed class JavaExternalJarSourceOpenCommand :
        JavaExternalJarSourceOpenCommandBase
    {
        [ImportingConstructor]
        public JavaExternalJarSourceOpenCommand(UnconfiguredProject project,
            [Import(typeof(SVsServiceProvider))] IServiceProvider services)
            : base(project, services, (long)VSConstants.VSStd97CmdID.Open) { }
    }
}
