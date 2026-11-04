using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using EnvDTE;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TemplateWizard;

namespace JavaLanguageSupport.ProjectSystem.Templates.Wizards
{
    /// <summary>
    /// F2E.3 hotfix: get the real CPS destination from IWizard2 when available.
    /// If Visual Studio only provides IWizard (or the CPS folder cannot be
    /// resolved in RunStarted), correct the generated item in the documented
    /// ProjectItemFinishedGenerating callback, before it is opened in the editor.
    /// </summary>
    public sealed class JavaItemTemplateWizard : IWizard2
    {
        private const string HeaderKey = "$javaPackageHeader$";
        private const string PackageKey = "$javaPackageName$";
        private static readonly string LogFile = Path.Combine(
            Path.GetTempPath(), "JavaCpsF2E3_TemplateWizard.log");
        private string _initialHeader = string.Empty;
        private bool _hierarchyResolved;
        private bool _initialized;

        public void RunStarted(object automationObject,
            Dictionary<string, string> replacementsDictionary,
            WizardRunKind runKind, object[] customParams)
        {
            Start(automationObject, replacementsDictionary, runKind, null, 0, false);
        }

        // IWizard2: the parentItemId identifies the actual destination node,
        // even when the selection in Solution Explorer has changed.
        public void RunStarted(object automationObject,
            Dictionary<string, string> replacementsDictionary,
            WizardRunKind runKind, object[] customParams,
            IVsProject project, uint parentItemId)
        {
            Start(automationObject, replacementsDictionary, runKind,
                project, parentItemId, true);
        }

        private void Start(object automationObject,
            Dictionary<string, string> replacements,
            WizardRunKind runKind, IVsProject project, uint parentItemId,
            bool viaWizard2)
        {
            if (replacements == null) throw new ArgumentNullException("replacements");
            if (!_initialized)
            {
                replacements[HeaderKey] = string.Empty;
                replacements[PackageKey] = string.Empty;
                _initialized = true;
            }
            Log("RunStarted " + (viaWizard2 ? "IWizard2" : "IWizard") +
                ", kind=" + runKind + ", hierarchy=" + (project != null) +
                ", parentItemId=" + parentItemId);
            if (runKind != WizardRunKind.AsNewItem) return;
            if (_hierarchyResolved && !viaWizard2) return;

            try
            {
                string projectFile = null;
                string directory = null;
                bool fromHierarchy = viaWizard2 && project != null &&
                    TryHierarchyDestination(project, parentItemId,
                        out projectFile, out directory);
                if (!fromHierarchy && !TryLocateDestination(automationObject as DTE,
                    replacements, out projectFile, out directory))
                {
                    Log("RunStarted: destination unavailable; awaiting ProjectItemFinishedGenerating");
                    return;
                }

                string header, packageName, error;
                if (JavaTemplatePackageContext.TryGetHeader(projectFile, directory,
                    out header, out packageName, out error))
                {
                    replacements[HeaderKey] = header;
                    replacements[PackageKey] = packageName;
                    _initialHeader = header;
                    _hierarchyResolved = fromHierarchy;
                    Log("RunStarted: project=" + projectFile +
                        "; target=" + directory + "; package=" + packageName +
                        "; source=" + (fromHierarchy ? "hierarchy" : "DTE"));
                }
                else
                {
                    Log("RunStarted: unresolved package; target=" + directory +
                        "; reason=" + error +
                        "; awaiting ProjectItemFinishedGenerating");
                }
            }
            catch (Exception ex)
            {
                // A diagnostic failure must never block creation of a Java file.
                Log("RunStarted ERROR " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool TryHierarchyDestination(IVsProject project,
            uint parentItemId, out string projectFile, out string targetDirectory)
        {
            projectFile = null;
            targetDirectory = null;
            if (project == null) return false;
            try
            {
                const uint rootItemId = 0xFFFFFFFEu; // VSITEMID_ROOT
                string rootPath;
                if (project.GetMkDocument(rootItemId, out rootPath) < 0 ||
                    string.IsNullOrWhiteSpace(rootPath) ||
                    !rootPath.EndsWith(".javaproj", StringComparison.OrdinalIgnoreCase))
                    return false;

                projectFile = Path.GetFullPath(rootPath);
                string parentPath;
                if (parentItemId == rootItemId)
                {
                    targetDirectory = Path.GetDirectoryName(projectFile);
                    return true;
                }
                if (project.GetMkDocument(parentItemId, out parentPath) < 0 ||
                    string.IsNullOrWhiteSpace(parentPath)) return false;

                parentPath = Path.GetFullPath(parentPath);
                if (Directory.Exists(parentPath))
                    targetDirectory = parentPath;
                else if (File.Exists(parentPath))
                    targetDirectory = Path.GetDirectoryName(parentPath);
                else
                    return false;
                return !string.IsNullOrEmpty(targetDirectory);
            }
            catch (Exception ex) when (ex is COMException || ex is ArgumentException ||
                ex is IOException || ex is NotSupportedException)
            {
                Log("IWizard2 hierarchy unavailable: " + ex.Message);
                return false;
            }
        }

        private static bool TryLocateDestination(DTE dte,
            Dictionary<string, string> replacements,
            out string projectFile, out string targetDirectory)
        {
            projectFile = null;
            targetDirectory = null;
            if (dte == null) return false;
            try
            {
                SelectedItems selected = dte.SelectedItems;
                if (selected != null && selected.Count == 1)
                {
                    SelectedItem node = selected.Item(1);
                    ProjectItem item = node == null ? null : node.ProjectItem;
                    Project candidate = item == null ?
                        (node == null ? null : node.Project) : item.ContainingProject;
                    if (TryProjectFile(candidate, out projectFile))
                    {
                        string explicitTarget;
                        if (TryDestinationToken(replacements, out explicitTarget))
                        {
                            targetDirectory = explicitTarget;
                            return true;
                        }
                        if (item != null && TryItemDirectory(item, out targetDirectory))
                            return true;
                        targetDirectory = Path.GetDirectoryName(projectFile);
                        return true;
                    }
                }

                object[] active = dte.ActiveSolutionProjects as object[];
                if (active != null && active.Length == 1 &&
                    TryProjectFile(active[0] as Project, out projectFile))
                {
                    if (!TryDestinationToken(replacements, out targetDirectory))
                        targetDirectory = Path.GetDirectoryName(projectFile);
                    return true;
                }
            }
            catch (Exception ex) when (ex is COMException || ex is ArgumentException ||
                ex is IOException || ex is NotSupportedException)
            {
                Log("DTE destination unavailable: " + ex.Message);
            }
            return false;
        }

        private static bool TryDestinationToken(Dictionary<string, string> replacements,
            out string directory)
        {
            directory = null;
            string candidate;
            if (replacements.TryGetValue("$destinationdirectory$", out candidate) &&
                !string.IsNullOrWhiteSpace(candidate) && Path.IsPathRooted(candidate) &&
                Directory.Exists(candidate))
            {
                directory = Path.GetFullPath(candidate);
                return true;
            }
            return false;
        }

        private static bool TryProjectFile(Project project, out string path)
        {
            path = null;
            if (project == null) return false;
            try
            {
                string candidate = project.FullName;
                if (string.IsNullOrWhiteSpace(candidate) ||
                    !candidate.EndsWith(".javaproj", StringComparison.OrdinalIgnoreCase))
                    return false;
                path = Path.GetFullPath(candidate);
                return true;
            }
            catch (Exception ex) when (ex is COMException || ex is ArgumentException ||
                ex is IOException || ex is NotSupportedException)
            {
                return false;
            }
        }

        private static bool TryItemDirectory(ProjectItem item, out string path)
        {
            path = null;
            if (item == null) return false;
            try
            {
                if (item.Properties != null)
                {
                    Property property = item.Properties.Item("FullPath");
                    string fullPath = property == null ? null : property.Value as string;
                    if (!string.IsNullOrWhiteSpace(fullPath) && Directory.Exists(fullPath))
                    {
                        path = Path.GetFullPath(fullPath);
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is COMException || ex is ArgumentException ||
                ex is IOException || ex is NotSupportedException)
            {
                // Some CPS item types do not expose FullPath.
            }
            try
            {
                if (item.FileCount < 1) return false;
                string file = item.FileNames[1];
                if (string.IsNullOrWhiteSpace(file)) return false;
                if (Directory.Exists(file)) path = Path.GetFullPath(file);
                else if (File.Exists(file)) path = Path.GetDirectoryName(Path.GetFullPath(file));
                return path != null;
            }
            catch (Exception ex) when (ex is COMException || ex is ArgumentException ||
                ex is IOException || ex is NotSupportedException)
            {
                return false;
            }
        }

        public void ProjectItemFinishedGenerating(ProjectItem projectItem)
        {
            try
            {
                if (projectItem == null || projectItem.FileCount < 1)
                {
                    Log("ProjectItemFinishedGenerating: no file item");
                    return;
                }
                string file = projectItem.FileNames[1];
                if (string.IsNullOrWhiteSpace(file) ||
                    !file.EndsWith(".java", StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(file))
                {
                    Log("ProjectItemFinishedGenerating: Java file missing " + file);
                    return;
                }
                string projectFile;
                if (!TryProjectFile(projectItem.ContainingProject, out projectFile))
                {
                    Log("ProjectItemFinishedGenerating: .javaproj unavailable for " + file);
                    return;
                }
                string header, packageName, error;
                if (!JavaTemplatePackageContext.TryGetHeader(projectFile,
                    Path.GetDirectoryName(Path.GetFullPath(file)),
                    out header, out packageName, out error))
                {
                    Log("ProjectItemFinishedGenerating: not resolved: " + error);
                    return;
                }

                if (header.Length == 0)
                {
                    Log("ProjectItemFinishedGenerating: default package, no change");
                    return;
                }

                byte[] bytes = File.ReadAllBytes(file);
                bool hasUtf8Bom = bytes.Length >= 3 && bytes[0] == 0xEF &&
                    bytes[1] == 0xBB && bytes[2] == 0xBF;
                // Never rewrite UTF-16, binary or a file that cannot be decoded as UTF-8.
                if ((bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) ||
                    (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF))
                {
                    Log("ProjectItemFinishedGenerating: non-UTF8 source skipped");
                    return;
                }
                string original = new UTF8Encoding(false, true).GetString(bytes,
                    hasUtf8Bom ? 3 : 0, bytes.Length - (hasUtf8Bom ? 3 : 0));
                string newLine = original.Contains("\r\n") ? "\r\n" : "\n";
                string correctHeader = header.Replace("\r\n", newLine);
                string previousHeader = _initialHeader.Replace("\r\n", newLine);
                if (original.StartsWith(correctHeader, StringComparison.Ordinal))
                {
                    Log("ProjectItemFinishedGenerating: already correct, package=" + packageName);
                    return;
                }
                string changed;
                if (previousHeader.Length > 0 &&
                    original.StartsWith(previousHeader, StringComparison.Ordinal))
                {
                    // The earlier DTE selection was stale: correct only our own header.
                    changed = correctHeader + original.Substring(previousHeader.Length);
                }
                else if (original.TrimStart().StartsWith("package ", StringComparison.Ordinal))
                {
                    // Do not overwrite an unrelated, existing package statement.
                    Log("ProjectItemFinishedGenerating: existing different package; skipped");
                    return;
                }
                else
                {
                    changed = correctHeader + original;
                }

                // This IWizard callback occurs after generation and before editor open.
                File.WriteAllText(file, changed, new UTF8Encoding(hasUtf8Bom));
                Log("ProjectItemFinishedGenerating: added/corrected package=" + packageName +
                    "; file=" + file);
            }
            catch (Exception ex)
            {
                Log("ProjectItemFinishedGenerating ERROR " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Log(string message)
        {
            try
            {
                File.AppendAllText(LogFile,
                    DateTime.UtcNow.ToString("o") + " | " + message + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch (Exception) { /* Optional diagnostic, never block item creation. */ }
        }

        public void ProjectFinishedGenerating(Project project) { }
        public void BeforeOpeningFile(ProjectItem projectItem) { }
        public void RunFinished() { Log("RunFinished"); }
        public bool ShouldAddProjectItem(string filePath) { return true; }
    }
}
