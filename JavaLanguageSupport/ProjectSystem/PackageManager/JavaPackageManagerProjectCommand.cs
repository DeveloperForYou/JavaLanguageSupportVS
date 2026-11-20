using System;
using System.IO;
using System.Runtime.InteropServices;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace JavaLanguageSupport.ProjectSystem.PackageManager
{
    /// <summary>F4.F.2: resolve the ACTUALLY selected CPS project node.</summary>
    internal static class JavaPackageManagerProjectCommand
    {
        internal static bool TryGetSelectedProject(IServiceProvider services, out string projectFile)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            projectFile = null;
            IVsMonitorSelection monitor = services.GetService(typeof(SVsShellMonitorSelection))
                as IVsMonitorSelection;
            if (monitor != null)
            {
                IntPtr hierarchyPtr = IntPtr.Zero;
                IntPtr containerPtr = IntPtr.Zero;
                try
                {
                    uint itemId;
                    IVsMultiItemSelect multi;
                    if (ErrorHandler.Succeeded(monitor.GetCurrentSelection(
                        out hierarchyPtr, out itemId, out multi, out containerPtr)))
                    {
                        IVsHierarchy hierarchy = null;
                        if (multi != null)
                        {
                            uint count;
                            int oneHierarchy;
                            if (ErrorHandler.Failed(multi.GetSelectionInfo(
                                out count, out oneHierarchy)) || count != 1)
                                return false;
                            if (itemId == VSConstants.VSITEMID_SELECTION || hierarchyPtr == IntPtr.Zero)
                            {
                                var items = new VSITEMSELECTION[1];
                                if (ErrorHandler.Failed(multi.GetSelectedItems(0, 1, items)))
                                    return false;
                                itemId = items[0].itemid;
                                hierarchy = items[0].pHier;
                            }
                        }
                        if (itemId != VSConstants.VSITEMID_ROOT) return false;
                        if (hierarchy == null && hierarchyPtr != IntPtr.Zero)
                            hierarchy = Marshal.GetObjectForIUnknown(hierarchyPtr) as IVsHierarchy;
                        if (hierarchy == null) return false;
                        object extObject;
                        string candidate = null;
                        if (ErrorHandler.Succeeded(hierarchy.GetProperty(
                            VSConstants.VSITEMID_ROOT,
                            (int)__VSHPROPID.VSHPROPID_ExtObject, out extObject)))
                        {
                            var project = extObject as Project;
                            if (project != null) candidate = project.FullName;
                        }
                        if (string.IsNullOrWhiteSpace(candidate))
                            hierarchy.GetCanonicalName(VSConstants.VSITEMID_ROOT, out candidate);
                        return IsJavaProject(candidate, out projectFile);
                    }
                }
                catch (Exception ex) when (ex is COMException || ex is IOException ||
                    ex is ArgumentException || ex is NotSupportedException ||
                    ex is UnauthorizedAccessException || ex is InvalidCastException) { return false; }
                finally
                {
                    if (hierarchyPtr != IntPtr.Zero) Marshal.Release(hierarchyPtr);
                    if (containerPtr != IntPtr.Zero) Marshal.Release(containerPtr);
                }
            }
            // Some CPS selection providers expose only DTE.SelectedItems.
            try
            {
                var dte = services.GetService(typeof(SDTE)) as DTE;
                if (dte == null || dte.SelectedItems == null || dte.SelectedItems.Count != 1)
                    return false;
                SelectedItem selected = dte.SelectedItems.Item(1);
                if (selected == null || selected.ProjectItem != null || selected.Project == null)
                    return false;
                return IsJavaProject(selected.Project.FullName, out projectFile);
            }
            catch (Exception ex) when (ex is COMException || ex is IOException ||
                ex is ArgumentException || ex is NotSupportedException) { return false; }
        }

        private static bool IsJavaProject(string candidate, out string projectFile)
        {
            projectFile = null;
            if (string.IsNullOrWhiteSpace(candidate) ||
                !string.Equals(Path.GetExtension(candidate), ".javaproj",
                    StringComparison.OrdinalIgnoreCase)) return false;
            string path = Path.GetFullPath(candidate);
            if (!File.Exists(path)) return false;
            projectFile = path;
            return true;
        }
    }
}
