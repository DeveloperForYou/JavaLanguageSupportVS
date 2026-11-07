using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace JavaLanguageSupport.LanguageServer
{
    /// <summary>
    /// The loaded CPS Java projects are the source of truth for project-mode
    /// LSP initialization. This class never opens documents or starts processes.
    /// </summary>
    internal static class JavaCpsWorkspaceContext
    {
        private static readonly object Sync = new object();
        private static readonly HashSet<string> Projects =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal static void ReplaceProjects(IEnumerable<string> projectFiles)
        {
            lock (Sync)
            {
                Projects.Clear();
                foreach (string candidate in projectFiles ?? Enumerable.Empty<string>())
                {
                    string full;
                    if (TryProjectFile(candidate, out full)) Projects.Add(full);
                }
            }
        }

        internal static void AddProject(string projectFile)
        {
            string full;
            if (!TryProjectFile(projectFile, out full)) return;
            lock (Sync) Projects.Add(full);
        }

        internal static void RemoveProject(string projectFile)
        {
            if (string.IsNullOrWhiteSpace(projectFile)) return;
            lock (Sync)
            {
                try { Projects.Remove(Path.GetFullPath(projectFile)); }
                catch (ArgumentException) { }
            }
        }

        internal static void Clear()
        {
            lock (Sync) Projects.Clear();
        }

        internal static bool ContainsProject(string projectFile)
        {
            if (string.IsNullOrWhiteSpace(projectFile)) return false;
            lock (Sync)
            {
                try { return Projects.Contains(Path.GetFullPath(projectFile)); }
                catch (ArgumentException) { return false; }
            }
        }

        internal static string ProjectRoot
        {
            get
            {
                lock (Sync)
                {
                    string project = Projects.OrderBy(p => p,
                        StringComparer.OrdinalIgnoreCase).FirstOrDefault(File.Exists);
                    return project == null ? null : Path.GetDirectoryName(project);
                }
            }
        }

        /// <summary>
        /// VS LSP defaults rootUri to the Open Folder root. In a CPS solution
        /// there may be no Open Folder at all, so supply the real Java project
        /// root before initialize reaches JDT LS. No other capabilities change.
        /// </summary>
        internal static JToken PrepareInitialize(JToken original)
        {
            var request = original as JObject;
            string root = ProjectRoot;
            if (request == null || string.IsNullOrWhiteSpace(root)) return original;

            var clone = (JObject)request.DeepClone();
            string folder = root.TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string uri = new Uri(folder).AbsoluteUri;
            clone["rootPath"] = root;
            clone["rootUri"] = uri;
            // Do not advertise workspaceFolders support on behalf of VS.
            // Populate folders only when its own client capabilities allow it.
            if ((bool?)clone.SelectToken("capabilities.workspace.workspaceFolders") == true)
            {
                clone["workspaceFolders"] = new JArray(new JObject {
                    ["uri"] = uri,
                    ["name"] = Path.GetFileName(root.TrimEnd('\\', '/'))
                });
            }
            return clone;
        }

        private static bool TryProjectFile(string candidate, out string full)
        {
            full = null;
            try
            {
                if (string.IsNullOrWhiteSpace(candidate) ||
                    !string.Equals(Path.GetExtension(candidate), ".javaproj",
                        StringComparison.OrdinalIgnoreCase)) return false;
                string path = Path.GetFullPath(candidate);
                if (!File.Exists(path)) return false;
                full = path;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException ||
                ex is NotSupportedException || ex is IOException ||
                ex is UnauthorizedAccessException) { return false; }
        }
    }
}
