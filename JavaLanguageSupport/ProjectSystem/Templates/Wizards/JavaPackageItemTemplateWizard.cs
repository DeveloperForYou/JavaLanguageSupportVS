using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;
using EnvDTE;
using JavaLanguageSupport.ProjectSystem.Packages;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TemplateWizard;

namespace JavaLanguageSupport.ProjectSystem.Templates.Wizards
{
    /// <summary>
    /// F2F.14: item template entry point in Aggiungi > Nuovo elemento.
    /// This template creates a real Java package folder, not a generated file.
    /// The package dialog and MSBuild Folder writer are shared with the native command.
    /// </summary>
    public sealed class JavaPackageItemTemplateWizard : IWizard2
    {
        private bool _executed;

        public void RunStarted(object automationObject,
            Dictionary<string, string> replacementsDictionary,
            WizardRunKind runKind, object[] customParams)
        {
            Run(automationObject as DTE, null, VSConstants.VSITEMID_NIL, runKind);
        }

        public void RunStarted(object automationObject,
            Dictionary<string, string> replacementsDictionary,
            WizardRunKind runKind, object[] customParams,
            IVsProject project, uint parentItemId)
        {
            Run(automationObject as DTE, project, parentItemId, runKind);
        }

        private void Run(DTE dte, IVsProject project, uint parentItemId,
            WizardRunKind runKind)
        {
            if (_executed || runKind != WizardRunKind.AsNewItem) return;
            ThreadHelper.ThrowIfNotOnUIThread();
            string projectFile, selectedDirectory;
            bool isProjectNode;
            if (!TryLocateContext(dte, project, parentItemId,
                out projectFile, out selectedDirectory, out isProjectNode))
                throw new WizardCancelledException(
                    "Non riesco a individuare il progetto .javaproj o la cartella selezionata.");

            // Mark before showing WPF: on some hosts both overloads may be called.
            _executed = true;
            string projectDirectory = Path.GetDirectoryName(projectFile);
            string buildHint = ReadBuildHint(projectFile);
            string baseDirectory, sourceRoot, existingPackage, error;
            if (!JavaPackageCreationService.TryGetBase(projectDirectory,
                selectedDirectory, isProjectNode, buildHint,
                out baseDirectory, out sourceRoot, out existingPackage, out error))
                throw new WizardCancelledException(error ??
                    "Cartella non valida per un package Java.");

            var dialog = new JavaNewPackageDialog(projectDirectory,
                selectedDirectory, isProjectNode, buildHint, sourceRoot, existingPackage);
            // JavaNewPackageDialog already handles the Visual Studio owner/theme.
            if (dialog.ShowDialog() != true)
                throw new WizardCancelledException("Creazione package annullata.");

            var chosenBase = dialog.SelectedBaseDirectory;
            JavaPackageCreationPlan plan;
            if (string.IsNullOrWhiteSpace(chosenBase) ||
                !JavaPackageCreationService.TryPlan(projectDirectory,
                    chosenBase, dialog.UseProjectNode, dialog.RelativePackageName,
                    buildHint, out plan, out error))
                throw new WizardCancelledException(error ?? "Package Java non valido.");

            try
            {
                // F2I5-CPS-CREATION-SYNC
                string[] newlyCreated = JavaWorkspaceCreationSync.MissingDirectories(
                    projectFile, plan.TargetDirectory);
                Directory.CreateDirectory(plan.TargetDirectory);
                string includeError;
                bool included = JavaEmptyPackageProjectItems.TryInclude(projectFile,
                    plan.TargetDirectory, out includeError);
                if (!included)
                {
                    // The directory may have been created. Never claim rollback.
                    VsShellUtilities.ShowMessageBox(ServiceProvider.GlobalProvider,
                        "La cartella e' stata creata, ma non registrata nel .javaproj.\n\n" +
                        includeError, "Package Java",
                        OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK,
                        OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                }
                if (included)
                    JavaWorkspaceCreationSync.QueueCreatedDirectories(
                        projectFile, newlyCreated);
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException ||
                ex is UnauthorizedAccessException || ex is NotSupportedException)
            {
                throw new WizardCancelledException("Creazione package non riuscita: " +
                    ex.Message);
            }
        }

        private static bool TryLocateContext(DTE dte, IVsProject project, uint itemId,
            out string projectFile, out string selectedDirectory,
            out bool isProjectNode)
        {
            projectFile = null;
            selectedDirectory = null;
            isProjectNode = false;
            if (project != null)
            {
                try
                {
                    string root;
                    // The CPS-provided hierarchy is authoritative when IWizard2 is used.
                    if (ErrorHandler.Succeeded(project.GetMkDocument(
                        VSConstants.VSITEMID_ROOT, out root)) &&
                        TryProject(root, out projectFile))
                    {
                        string projectDir = Path.GetDirectoryName(projectFile);
                        isProjectNode = itemId == VSConstants.VSITEMID_ROOT;
                        if (isProjectNode)
                        {
                            selectedDirectory = projectDir;
                            return true;
                        }
                        string node;
                        if (ErrorHandler.Succeeded(project.GetMkDocument(itemId, out node)) &&
                            TryDirectory(projectDir, node, out selectedDirectory)) return true;
                    }
                }
                catch (Exception ex) when (ex is COMException ||
                    ex is IOException || ex is ArgumentException ||
                    ex is NotSupportedException || ex is UnauthorizedAccessException) { }
            }

            // Some Visual Studio builds call IWizard rather than IWizard2.
            if (dte == null) return false;
            try
            {
                SelectedItems selection = dte.SelectedItems;
                if (selection != null && selection.Count == 1)
                {
                    SelectedItem selected = selection.Item(1);
                    ProjectItem folder = selected == null ? null : selected.ProjectItem;
                    Project containing = folder != null ? folder.ContainingProject :
                        (selected == null ? null : selected.Project);
                    if (containing != null && TryProject(containing.FullName, out projectFile))
                    {
                        string projectDir = Path.GetDirectoryName(projectFile);
                        isProjectNode = folder == null;
                        if (folder == null)
                        {
                            selectedDirectory = projectDir;
                            return true;
                        }
                        string candidate;
                        if (TryItemDirectory(folder, out candidate) &&
                            TryDirectory(projectDir, candidate, out selectedDirectory))
                            return true;
                    }
                }
                object[] active = dte.ActiveSolutionProjects as object[];
                if (active != null && active.Length == 1)
                {
                    var activeProject = active[0] as Project;
                    if (activeProject != null &&
                        TryProject(activeProject.FullName, out projectFile))
                    {
                        selectedDirectory = Path.GetDirectoryName(projectFile);
                        isProjectNode = true;
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is COMException || ex is ArgumentException ||
                ex is IOException || ex is NotSupportedException ||
                ex is UnauthorizedAccessException) { }
            return false;
        }

        private static bool TryItemDirectory(ProjectItem item, out string path)
        {
            path = null;
            try
            {
                if (item.Properties != null)
                {
                    var property = item.Properties.Item("FullPath");
                    var value = property == null ? null : property.Value as string;
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        path = value;
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is COMException || ex is ArgumentException) { }
            try
            {
                if (item.FileCount < 1) return false;
                path = item.FileNames[1];
                return !string.IsNullOrWhiteSpace(path);
            }
            catch (Exception ex) when (ex is COMException || ex is ArgumentException) { }
            return false;
        }

        private static bool TryProject(string candidate, out string path)
        {
            path = null;
            try
            {
                if (string.IsNullOrWhiteSpace(candidate) ||
                    !candidate.EndsWith(".javaproj", StringComparison.OrdinalIgnoreCase))
                    return false;
                path = Path.GetFullPath(candidate);
                return File.Exists(path);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException ||
                ex is NotSupportedException || ex is UnauthorizedAccessException) { return false; }
        }

        private static bool TryDirectory(string projectDirectory, string candidate,
            out string directory)
        {
            directory = null;
            if (string.IsNullOrWhiteSpace(candidate)) return false;
            try
            {
                Uri uri;
                if (Uri.TryCreate(candidate, UriKind.Absolute, out uri) && uri.IsFile)
                    candidate = uri.LocalPath;
                var fullPath = Path.GetFullPath(Path.IsPathRooted(candidate)
                    ? candidate : Path.Combine(projectDirectory, candidate));
                // For an item selected in Solution Explorer, use its parent folder.
                if (File.Exists(fullPath)) fullPath = Path.GetDirectoryName(fullPath);
                var root = Path.GetFullPath(projectDirectory).TrimEnd('\\', '/');
                if (!Directory.Exists(fullPath) ||
                    !(string.Equals(fullPath.TrimEnd('\\', '/'), root,
                        StringComparison.OrdinalIgnoreCase) ||
                    fullPath.StartsWith(root + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))) return false;
                directory = fullPath.TrimEnd('\\', '/');
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException ||
                ex is NotSupportedException || ex is UnauthorizedAccessException) { return false; }
        }

        private static string ReadBuildHint(string projectFile)
        {
            try
            {
                var settings = new XmlReaderSettings {
                    XmlResolver = null, DtdProcessing = DtdProcessing.Prohibit };
                using (var reader = XmlReader.Create(projectFile, settings))
                {
                    var document = XDocument.Load(reader);
                    var node = document.Descendants().FirstOrDefault(e =>
                        e.Name.LocalName == "JavaBuildSystem");
                    return node == null ? null : node.Value.Trim();
                }
            }
            catch (Exception ex) when (ex is IOException || ex is XmlException ||
                ex is UnauthorizedAccessException) { return null; }
        }

        // VS requires a file-like ProjectItem in a vstemplate. It is a marker
        // only: prevent the host from generating/inserting that fake file.
        public bool ShouldAddProjectItem(string filePath) { return false; }
        public void ProjectItemFinishedGenerating(ProjectItem projectItem) { }
        public void ProjectFinishedGenerating(Project project) { }
        public void BeforeOpeningFile(ProjectItem projectItem) { }
        public void RunFinished() { }
    }
}
