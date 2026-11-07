using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio;

namespace JavaLanguageSupport.ProjectSystem.Packages
{
    /// <summary>
    /// F2F.1 native Solution Explorer context command. Does not touch template
    /// creation, JavaPackageResolver, CPS project manifests, or JDT LS.
    /// </summary>
    internal static class JavaNewPackageCommand
    {
        private sealed class SelectionContext
        {
            public Project Project;
            public string ProjectFile;
            public ProjectItem Folder;
            public string ProjectDirectory;
            public string SelectedDirectory;
            public bool IsProjectNode;
            public string BuildHint;
        }

        public static bool CanExecute(IServiceProvider services)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            SelectionContext context;
            return TryGetContext(services, out context);
        }

        public static void Execute(IServiceProvider services)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            SelectionContext context;
            if (!TryGetContext(services, out context)) return;
            string root, sourceRoot, existing, error;
            if (!JavaPackageCreationService.TryGetBase(context.ProjectDirectory,
                context.SelectedDirectory, context.IsProjectNode, context.BuildHint,
                out root, out sourceRoot, out existing, out error))
            {
                ShowError(services, error ?? "Impossibile determinare la source root.");
                return;
            }

            // Pass the resolved base, not the raw CPS selection. The user can
            // change source root and starting package inside the dialog.
            var dialog = new JavaNewPackageDialog(context.ProjectDirectory,
                root, context.IsProjectNode, context.BuildHint, sourceRoot, existing);
            var shell = services.GetService(typeof(SVsUIShell)) as IVsUIShell;
            IntPtr hwnd;
            if (shell != null && ErrorHandler.Succeeded(shell.GetDialogOwnerHwnd(out hwnd)) &&
                hwnd != IntPtr.Zero)
                new System.Windows.Interop.WindowInteropHelper(dialog).Owner = hwnd;

            if (dialog.ShowDialog() != true) return;
            JavaPackageCreationPlan plan;
            if (!JavaPackageCreationService.TryPlan(context.ProjectDirectory,
                dialog.SelectedBaseDirectory, dialog.UseProjectNode,
                dialog.RelativePackageName, context.BuildHint, out plan, out error))
            {
                ShowError(services, error ?? "Package Java non valido.");
                return;
            }
            try
            {
                // F2F1-CPS-FOLDER-FIX-BEGIN
                // F2F1-EMPTY-PACKAGE-VISIBLE: aggiunge un item Folder esplicito
                // al .javaproj, senza chiamare ProjectItems.AddFolder.
                // F2I5-CPS-CREATION-SYNC: capture genuinely new folders.
                string[] newlyCreated = JavaWorkspaceCreationSync.MissingDirectories(
                    context.ProjectFile, plan.TargetDirectory);
                Directory.CreateDirectory(plan.TargetDirectory);
                string includeError;
                bool included = JavaEmptyPackageProjectItems.TryInclude(
                    context.ProjectFile, plan.TargetDirectory, out includeError);
                if (!included)
                {
                    VsShellUtilities.ShowMessageBox(services,
                        "Package '" + plan.FullPackageName + "' creato sul disco, ma non " +
                        "ancora registrato nel file .javaproj.\n\n" + includeError +
                        "\n\nAggiungi un file nel package oppure registra manualmente " +
                        "<Folder Include=\"...\" /> nel progetto.",
                        "Nuovo package Java", OLEMSGICON.OLEMSGICON_WARNING,
                        OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                }
                if (included)
                    JavaWorkspaceCreationSync.QueueCreatedDirectories(
                        context.ProjectFile, newlyCreated);
                // F2F1-CPS-FOLDER-FIX-END
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                ex is COMException || ex is ArgumentException || ex is NotSupportedException)
            {
                ShowError(services, "Creazione del package non riuscita: " + ex.Message);
            }
        }

        // F2H1-CONTEXT-ACCESSOR: expose only the CPS selection already validated
        // by F2F.7; no second selection resolver or new menu diagnostics.
        internal static bool TryGetSelectedJavaPackage(IServiceProvider services,
            out string projectFile, out string selectedDirectory,
            out string packageName, out string sourceRoot)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            projectFile = selectedDirectory = packageName = sourceRoot = null;
            SelectionContext context;
            if (!TryGetContext(services, out context) || context.IsProjectNode)
                return false;
            JavaPackageInfo info;
            string error;
            if (!JavaPackageResolver.TryResolveForDirectory(context.ProjectDirectory,
                context.SelectedDirectory, out info, out error, context.BuildHint) ||
                info.IsDefaultPackage)
                return false;
            projectFile = context.ProjectFile;
            selectedDirectory = info.DirectoryPath.TrimEnd('\\', '/');
            packageName = info.Name;
            sourceRoot = info.SourceRoot.FullPath;
            return true;
        }

        private static void ShowError(IServiceProvider services, string text)
        {
            VsShellUtilities.ShowMessageBox(services, text, "Nuovo package Java",
                OLEMSGICON.OLEMSGICON_CRITICAL, OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }

        // F2F1-MENU-HOTFIX-HIERARCHY-BEGIN
        // Query the authoritative Solution Explorer selection, not only DTE.SelectedItems.
        private static bool TryGetContextFromHierarchy(IServiceProvider services,
            out SelectionContext context)
        {
            context = null;
            IVsMonitorSelection selection =
                services.GetService(typeof(SVsShellMonitorSelection)) as IVsMonitorSelection;
            if (selection == null) return false;
            IntPtr hierarchyPtr = IntPtr.Zero;
            IntPtr selectionPtr = IntPtr.Zero;
            try
            {
                // F2F5-SINGLE-SELECT-CPS: a CPS folder can arrive with
                // VSITEMID_SELECTION and an IVsMultiItemSelect even when the
                // user selected ONE folder. Do not discard that selection.
                uint itemId;
                IVsMultiItemSelect multi;
                if (ErrorHandler.Failed(selection.GetCurrentSelection(out hierarchyPtr,
                    out itemId, out multi, out selectionPtr))) return false;

                IVsHierarchy hierarchy = null;
                if (multi != null)
                {
                    uint count;
                    int singleHierarchy;
                    if (ErrorHandler.Failed(multi.GetSelectionInfo(
                            out count, out singleHierarchy)) || count != 1)
                        return false;

                    if (itemId == VSConstants.VSITEMID_SELECTION ||
                        hierarchyPtr == IntPtr.Zero)
                    {
                        var selected = new VSITEMSELECTION[1];
                        if (ErrorHandler.Failed(multi.GetSelectedItems(0, 1, selected)))
                            return false;
                        itemId = selected[0].itemid;
                        hierarchy = selected[0].pHier;
                    }
                }

                if (itemId == VSConstants.VSITEMID_NIL ||
                    itemId == VSConstants.VSITEMID_SELECTION) return false;
                if (hierarchy == null && hierarchyPtr != IntPtr.Zero)
                    hierarchy = Marshal.GetObjectForIUnknown(hierarchyPtr) as IVsHierarchy;
                if (hierarchy == null) return false;

                Project project = null;
                object projectObject;
                if (ErrorHandler.Succeeded(hierarchy.GetProperty(VSConstants.VSITEMID_ROOT,
                    (int)__VSHPROPID.VSHPROPID_ExtObject, out projectObject)))
                    project = projectObject as Project;

                string projectFile = null;
                try { if (project != null) projectFile = project.FullName; }
                catch (COMException) { }
                if (string.IsNullOrEmpty(projectFile) ||
                    !projectFile.EndsWith(".javaproj", StringComparison.OrdinalIgnoreCase))
                    hierarchy.GetCanonicalName(VSConstants.VSITEMID_ROOT, out projectFile);
                if (string.IsNullOrEmpty(projectFile) ||
                    !projectFile.EndsWith(".javaproj", StringComparison.OrdinalIgnoreCase))
                    return false;
                projectFile = Path.GetFullPath(projectFile);
                string projectDir = Path.GetDirectoryName(projectFile);
                bool isRoot = itemId == VSConstants.VSITEMID_ROOT;
                string folderPath = projectDir;
                ProjectItem folder = null;
                if (!isRoot)
                {
                    object folderObject;
                    if (ErrorHandler.Succeeded(hierarchy.GetProperty(itemId,
                        (int)__VSHPROPID.VSHPROPID_ExtObject, out folderObject)))
                        folder = folderObject as ProjectItem;
                    // CPS may return a project-relative canonical name for a folder.
                    // Resolve against the .javaproj directory, never the devenv.exe CWD.
                    if (!TryGetHierarchyDirectory(hierarchy, itemId, folder,
                        projectDir, out folderPath))
                        return false;
                }
                string buildHint = ReadBuildHint(projectFile);
                string root, sourceRoot, existing, error;
                if (!JavaPackageCreationService.TryGetBase(projectDir, folderPath,
                    isRoot, buildHint, out root, out sourceRoot, out existing, out error))
                    return false;
                context = new SelectionContext
                {
                    Project = project, ProjectFile = projectFile, Folder = folder,
                    ProjectDirectory = projectDir,
                    SelectedDirectory = folderPath, IsProjectNode = isRoot,
                    BuildHint = buildHint
                };
                return true;
            }
            catch (Exception ex) when (ex is COMException || ex is ArgumentException ||
                ex is IOException || ex is NotSupportedException ||
                ex is UnauthorizedAccessException || ex is InvalidCastException)
            {
                return false;
            }
            finally
            {
                if (hierarchyPtr != IntPtr.Zero) Marshal.Release(hierarchyPtr);
                if (selectionPtr != IntPtr.Zero) Marshal.Release(selectionPtr);
            }
        }
        // F2F1-MENU-HOTFIX-HIERARCHY-END
        private static bool TryGetContext(IServiceProvider services, out SelectionContext context)
        {
            context = null;
            if (TryGetContextFromHierarchy(services, out context)) return true;
            DTE dte = services.GetService(typeof(SDTE)) as DTE;
            if (dte == null) return false;
            try
            {
                SelectedItems selection = dte.SelectedItems;
                if (selection == null || selection.Count != 1) return false;
                SelectedItem selected = selection.Item(1);
                if (selected == null) return false;
                ProjectItem folder = selected.ProjectItem;
                Project project = folder == null ? selected.Project : folder.ContainingProject;
                if (project == null) return false;
                string filename = project.FullName;
                if (string.IsNullOrWhiteSpace(filename) ||
                    !filename.EndsWith(".javaproj", StringComparison.OrdinalIgnoreCase))
                    return false;
                string projectDirectory = Path.GetDirectoryName(Path.GetFullPath(filename));
                string selectedDirectory = projectDirectory;
                bool projectNode = folder == null;
                if (!projectNode && !TryGetFolderPath(folder, out selectedDirectory))
                    return false;
                string buildHint = ReadBuildHint(filename);
                string root, sourceRoot, existing, error;
                if (!JavaPackageCreationService.TryGetBase(projectDirectory,
                    selectedDirectory, projectNode, buildHint, out root,
                    out sourceRoot, out existing, out error)) return false;

                context = new SelectionContext
                {
                    Project = project,
                    ProjectFile = Path.GetFullPath(filename),
                    Folder = folder,
                    ProjectDirectory = projectDirectory,
                    SelectedDirectory = selectedDirectory,
                    IsProjectNode = projectNode,
                    BuildHint = buildHint
                };
                return true;
            }
            catch (Exception ex) when (ex is COMException || ex is ArgumentException ||
                ex is IOException || ex is NotSupportedException ||
                ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        // F2F.2: CPS folder nodes can be exposed as a ProjectItem, an IVsProject
        // document moniker or a canonical name. Use only paths inside this project.
        private static bool TryGetHierarchyDirectory(IVsHierarchy hierarchy, uint itemId,
            ProjectItem folder, string projectDirectory, out string directory)
        {
            directory = null;
            string candidate;
            if (folder != null && TryGetFolderPath(folder, out candidate) &&
                TryResolveDirectory(projectDirectory, candidate, out directory))
                return true;

            var vsProject = hierarchy as IVsProject;
            if (vsProject != null &&
                ErrorHandler.Succeeded(vsProject.GetMkDocument(itemId, out candidate)) &&
                TryResolveDirectory(projectDirectory, candidate, out directory))
                return true;

            if (ErrorHandler.Succeeded(hierarchy.GetCanonicalName(itemId, out candidate)) &&
                TryResolveDirectory(projectDirectory, candidate, out directory))
                return true;

            object saveName;
            if (ErrorHandler.Succeeded(hierarchy.GetProperty(itemId,
                    (int)__VSHPROPID.VSHPROPID_SaveName, out saveName)) &&
                TryResolveDirectory(projectDirectory, saveName as string, out directory))
                return true;

            // F2F3-CPS-BROWSE-PATH: use the folder's browse object (the same
            // object displayed by the Properties toolwindow) when CPS does not
            // expose an absolute GetMkDocument/canonical name.
            object browse;
            if (ErrorHandler.Succeeded(hierarchy.GetProperty(itemId,
                    (int)__VSHPROPID.VSHPROPID_BrowseObject, out browse)) && browse != null)
            {
                try
                {
                    var props = System.ComponentModel.TypeDescriptor.GetProperties(browse);
                    var fullPath = props.Find("FullPath", true);
                    if (fullPath != null)
                    {
                        object value = fullPath.GetValue(browse);
                        if (TryResolveDirectory(projectDirectory,
                            value == null ? null : Convert.ToString(value), out directory))
                            return true;
                    }
                }
                catch (Exception ex) when (ex is COMException ||
                    ex is ArgumentException || ex is NotSupportedException ||
                    ex is System.Reflection.TargetInvocationException) { }
            }

            // Last fallback: walk VSHPROPID_Parent and compose folder names.
            // This covers CPS nodes whose canonical name is a leaf only.
            var parts = new System.Collections.Generic.Stack<string>();
            uint current = itemId;
            for (int depth = 0; depth < 80 && current != VSConstants.VSITEMID_ROOT; depth++)
            {
                object partValue;
                if (ErrorHandler.Failed(hierarchy.GetProperty(current,
                    (int)__VSHPROPID.VSHPROPID_Name, out partValue)) ||
                    !(partValue is string)) break;
                string name = ((string)partValue).Trim();
                if (name.Length == 0 || name == "." || name == ".." ||
                    name.IndexOfAny(new[] { '\\', '/' }) >= 0) break;
                parts.Push(name);
                object parentValue;
                if (ErrorHandler.Failed(hierarchy.GetProperty(current,
                    (int)__VSHPROPID.VSHPROPID_Parent, out parentValue)) ||
                    parentValue == null) break;
                try
                {
                    current = parentValue is uint
                        ? (uint)parentValue
                        : unchecked((uint)Convert.ToInt64(parentValue));
                }
                catch (Exception ex) when (ex is FormatException ||
                    ex is InvalidCastException || ex is OverflowException) { break; }
            }
            if (current == VSConstants.VSITEMID_ROOT && parts.Count > 0 &&
                TryResolveDirectory(projectDirectory,
                    Path.Combine(parts.ToArray()), out directory))
                return true;
            return false;
        }

        private static bool TryResolveDirectory(string projectDirectory, string candidate,
            out string directory)
        {
            directory = null;
            if (string.IsNullOrWhiteSpace(candidate)) return false;
            try
            {
                candidate = candidate.Trim().Trim('"');
                Uri fileUri;
                if (Uri.TryCreate(candidate, UriKind.Absolute, out fileUri) && fileUri.IsFile)
                    candidate = fileUri.LocalPath;

                var root = Path.GetFullPath(projectDirectory).TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var path = Path.GetFullPath(Path.IsPathRooted(candidate)
                    ? candidate : Path.Combine(root, candidate));
                // An existing .java or other item must NEVER be treated as a package.
                if (!Directory.Exists(path) ||
                    !(string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
                      path.StartsWith(root + Path.DirectorySeparatorChar,
                          StringComparison.OrdinalIgnoreCase)))
                    return false;
                directory = path;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException ||
                ex is NotSupportedException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static bool TryGetFolderPath(ProjectItem item, out string path)
        {
            path = null;
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
                ex is IOException || ex is NotSupportedException) { }
            try
            {
                if (item.FileCount < 1) return false;
                string file = item.FileNames[1];
                if (string.IsNullOrWhiteSpace(file) || !Directory.Exists(file))
                    return false;
                path = Path.GetFullPath(file);
                return true;
            }
            catch (Exception ex) when (ex is COMException || ex is ArgumentException ||
                ex is IOException || ex is NotSupportedException) { return false; }
        }

        private static string ReadBuildHint(string projectFile)
        {
            try
            {
                var settings = new XmlReaderSettings
                {
                    XmlResolver = null, DtdProcessing = DtdProcessing.Prohibit
                };
                using (var reader = XmlReader.Create(projectFile, settings))
                {
                    XDocument document = XDocument.Load(reader);
                    var node = document.Descendants()
                        .FirstOrDefault(e => e.Name.LocalName == "JavaBuildSystem");
                    if (node == null) return null;
                    string value = node.Value.Trim();
                    return value == "Maven" || value == "Gradle" || value == "Plain" ||
                        value == "None" ? value : null;
                }
            }
            catch (Exception ex) when (ex is XmlException || ex is IOException ||
                ex is UnauthorizedAccessException) { return null; }
        }

    }
}
