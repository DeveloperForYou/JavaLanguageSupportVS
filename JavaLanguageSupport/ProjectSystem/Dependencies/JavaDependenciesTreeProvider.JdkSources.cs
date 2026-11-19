using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
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
    // F4.D.1.3: only the leaf nodes representing actual JDK classes are openable.
    // No physical file is attached to a virtual CPS node; opening extracts ONE
    // source from src.zip into a local, read-only cache on explicit user action.
    internal static class JavaJdkSourceTreeFlags
    {
        internal const string ClassNodeName = "JavaJdkSourceClass";
        internal static readonly ProjectTreeFlags ClassNode =
            ProjectTreeFlags.Create(ClassNodeName);
    }

    internal partial class JavaDependenciesTreeProvider
    {
        private string jdkSourceRegisteredProject;

        private void ClearJdkSourceRegistration()
        {
            if (string.IsNullOrEmpty(jdkSourceRegisteredProject)) return;
            JavaJdkSourceRegistry.Set(jdkSourceRegisteredProject, null);
            jdkSourceRegisteredProject = null;
        }

        private void RememberJdkSourceRegistration(string projectFile, string home)
        {
            ClearJdkSourceRegistration();
            if (string.IsNullOrEmpty(home)) return;
            JavaJdkSourceRegistry.Set(projectFile, home);
            jdkSourceRegisteredProject = projectFile;
        }
    }

    internal static class JavaJdkSourceRegistry
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, string> Homes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal static void Set(string projectFile, string home)
        {
            if (string.IsNullOrWhiteSpace(projectFile)) return;
            lock (Sync)
            {
                if (string.IsNullOrWhiteSpace(home)) Homes.Remove(projectFile);
                else Homes[projectFile] = home;
            }
        }

        internal static bool TryGet(string projectFile, out string home)
        {
            lock (Sync) return Homes.TryGetValue(projectFile, out home);
        }
    }

    internal sealed class JavaJdkSourceLocator
    {
        internal string Module;
        internal string Package;
        internal string Class;

        private static bool IsJavaName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 160) return false;
            // Modules/packages/classes emitted by the JDK image are ASCII here.
            // Reject separators, colon, dot-dot, spaces and shell metacharacters.
            if (!(char.IsLetter(name[0]) || name[0] == '_')) return false;
            return name.All(c => (c >= 'A' && c <= 'Z') ||
                (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_');
        }

        private static bool IsDottedName(string name)
        {
            return !string.IsNullOrEmpty(name) && name.Length <= 350 &&
                name.Split('.').All(IsJavaName);
        }

        internal static bool TryFrom(IImmutableSet<IProjectTree> nodes,
            out JavaJdkSourceLocator locator)
        {
            locator = null;
            if (nodes == null || nodes.Count != 1) return false;
            IProjectTree node = nodes.First();
            if (!node.Flags.Contains(JavaJdkSourceTreeFlags.ClassNodeName)) return false;
            IProjectTree package = node.Parent;
            IProjectTree classes = package == null ? null : package.Parent;
            IProjectTree module = classes == null ? null : classes.Parent;
            if (module == null || package == null || classes == null ||
                !classes.Caption.StartsWith("Classi (", StringComparison.Ordinal))
                return false;
            string pkg = package.Caption == "(package predefinito)" ? "" : package.Caption;
            if (!IsJavaName(node.Caption) || !IsDottedName(module.Caption) ||
                (pkg.Length != 0 && !IsDottedName(pkg))) return false;
            locator = new JavaJdkSourceLocator {
                Module = module.Caption, Package = pkg, Class = node.Caption
            };
            return true;
        }
    }

    internal static class JavaJdkSourceArchive
    {
        private const long MaxSourceBytes = 4L * 1024L * 1024L;

        internal static string OpenOne(string home, JavaJdkSourceLocator classInfo)
        {
            if (string.IsNullOrWhiteSpace(home))
                throw new FileNotFoundException("Il JDK non e' stato ancora rilevato.");
            string zip = Path.Combine(home, "lib", "src.zip");
            if (!File.Exists(zip)) zip = Path.Combine(home, "src.zip");
            if (!File.Exists(zip))
                throw new FileNotFoundException("src.zip non presente nel JDK selezionato.");

            var fileInfo = new FileInfo(zip);
            string key = zip.ToUpperInvariant() + "|" + fileInfo.Length + "|" +
                fileInfo.LastWriteTimeUtc.Ticks;
            string fingerprint;
            using (var sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                fingerprint = BitConverter.ToString(digest, 0, 12).Replace("-", "");
            }
            string relPackage = classInfo.Package.Replace('.', '/');
            string entryName = classInfo.Module + "/" +
                (relPackage.Length == 0 ? "" : relPackage + "/") +
                classInfo.Class + ".java";
            string cache = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
                "JavaLanguageSupportVS", "jdk-sources", fingerprint);
            string output = Path.Combine(cache, classInfo.Module,
                relPackage.Replace('/', Path.DirectorySeparatorChar),
                classInfo.Class + ".java");
            string fullCache = Path.GetFullPath(cache).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullOutput = Path.GetFullPath(output);
            if (!fullOutput.StartsWith(fullCache + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Percorso sorgente JDK non valido.");

            using (var stream = new FileStream(zip, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                ZipArchiveEntry entry = archive.GetEntry(entryName);
                if (entry == null)
                    throw new FileNotFoundException(
                        "Sorgente non presente in src.zip: " + classInfo.Class + ".java");
                if (entry.Length <= 0 || entry.Length > MaxSourceBytes)
                    throw new InvalidDataException("Dimensione sorgente JDK non valida.");

                if (File.Exists(fullOutput)) return fullOutput;
                // Refuse reparse-point redirection in the cache path.
                string dir = Path.GetDirectoryName(fullOutput);
                var pending = new Stack<string>();
                for (string part = dir; !string.IsNullOrEmpty(part) &&
                    part.Length >= fullCache.Length; part = Path.GetDirectoryName(part))
                {
                    pending.Push(part);
                    if (part.Equals(fullCache, StringComparison.OrdinalIgnoreCase)) break;
                }
                while (pending.Count != 0)
                {
                    string part = pending.Pop();
                    if (Directory.Exists(part))
                    {
                        if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                            throw new IOException("Cache sorgenti JDK reindirizzata.");
                    }
                    else Directory.CreateDirectory(part);
                }

                // Atomic move avoids partial files when two tabs open together.
                string temp = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    using (var source = entry.Open())
                    using (var destination = new FileStream(temp, FileMode.CreateNew,
                        FileAccess.Write, FileShare.None))
                    {
                        byte[] buffer = new byte[16384];
                        long copied = 0;
                        int size;
                        while ((size = source.Read(buffer, 0, buffer.Length)) != 0)
                        {
                            copied += size;
                            if (copied > MaxSourceBytes)
                                throw new InvalidDataException("src.zip supera il limite sorgente.");
                            destination.Write(buffer, 0, size);
                        }
                        if (copied == 0)
                            throw new InvalidDataException("Sorgente JDK vuoto.");
                    }
                    try { File.Move(temp, fullOutput); }
                    catch (IOException) { if (!File.Exists(fullOutput)) throw; }
                    if (File.Exists(fullOutput))
                        File.SetAttributes(fullOutput,
                            File.GetAttributes(fullOutput) | FileAttributes.ReadOnly);
                }
                finally
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
            }
            return fullOutput;
        }
    }

    internal abstract class JavaJdkSourceOpenCommandBase : IAsyncCommandGroupHandler
    {
        private readonly long[] commandIds;
        private readonly UnconfiguredProject project;
        private readonly IServiceProvider serviceProvider;

        protected JavaJdkSourceOpenCommandBase(UnconfiguredProject project,
            IServiceProvider serviceProvider, params long[] commandIds)
        {
            this.project = project;
            this.serviceProvider = serviceProvider;
            this.commandIds = commandIds;
        }

        private bool Matches(long id, IImmutableSet<IProjectTree> nodes,
            out JavaJdkSourceLocator locator)
        {
            locator = null;
            return commandIds.Contains(id) &&
                JavaJdkSourceLocator.TryFrom(nodes, out locator);
        }

        public Task<CommandStatusResult> GetCommandStatusAsync(
            IImmutableSet<IProjectTree> nodes, long commandId, bool focused,
            string commandText, CommandStatus progressiveStatus)
        {
            JavaJdkSourceLocator ignored;
            if (!Matches(commandId, nodes, out ignored))
                return Task.FromResult(CommandStatusResult.Unhandled);
            return Task.FromResult(new CommandStatusResult(true, commandText,
                progressiveStatus | CommandStatus.Enabled | CommandStatus.Supported));
        }

        public Task<bool> TryHandleCommandAsync(
            IImmutableSet<IProjectTree> nodes, long commandId, bool focused,
            long commandExecuteOptions, IntPtr variantArgIn, IntPtr variantArgOut)
        {
            JavaJdkSourceLocator location;
            if (!Matches(commandId, nodes, out location)) return Task.FromResult(false);

            // CPS can synchronously wait for command tasks on the VS UI thread.
            // Return immediately, do ZIP I/O off-thread, then switch to the UI
            // thread only to open the document. Do not launch a shell process.
            string projectPath = project.FullPath;
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    string path = await Task.Run(() =>
                    {
                        string home;
                        if (!JavaJdkSourceRegistry.TryGet(projectPath, out home))
                            throw new InvalidOperationException("JDK non ancora disponibile.");
                        return JavaJdkSourceArchive.OpenOne(home, location);
                    });
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    VsShellUtilities.OpenDocument(serviceProvider, path);
                }
                catch (Exception ex)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var bar = serviceProvider.GetService(typeof(SVsStatusbar)) as IVsStatusbar;
                    if (bar != null)
                    {
                        // No exception details or full JDK paths displayed.
                        string message = ex is FileNotFoundException ? ex.Message :
                            "Impossibile aprire il sorgente JDK (" + ex.GetType().Name + ").";
                        bar.SetText("Java: " + message);
                    }
                }
            });
            return Task.FromResult(true);
        }
    }

    // Command routing for tree activation by mouse, Enter and Project->Open.
    [ExportCommandGroup("{60481700-078B-11D1-AAF8-00A0C9055A90}")]
    [AppliesTo("JavaLanguageSupportCps")]
    [Order(1000)]
    internal sealed class JavaJdkSourceActivateCommand : JavaJdkSourceOpenCommandBase
    {
        [ImportingConstructor]
        public JavaJdkSourceActivateCommand(UnconfiguredProject project,
            [Import(typeof(SVsServiceProvider))] IServiceProvider services)
            : base(project, services, 2L, 3L) { }
    }

    [ExportCommandGroup("{5EFC7975-14BC-11CF-9B2B-00AA00573819}")]
    [AppliesTo("JavaLanguageSupportCps")]
    [Order(1000)]
    internal sealed class JavaJdkSourceOpenCommand : JavaJdkSourceOpenCommandBase
    {
        [ImportingConstructor]
        public JavaJdkSourceOpenCommand(UnconfiguredProject project,
            [Import(typeof(SVsServiceProvider))] IServiceProvider services)
            : base(project, services, (long)VSConstants.VSStd97CmdID.Open) { }
    }
}
