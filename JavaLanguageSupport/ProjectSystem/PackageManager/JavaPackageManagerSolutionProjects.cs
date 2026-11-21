using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace JavaLanguageSupport.ProjectSystem.PackageManager
{
    // F4F284-PROJECT-TARGET: a single loaded .javaproj is the authoritative target.
    // The browser never derives a target from the active editor or solution selection.
    internal sealed class JavaPackageProjectEntry
    {
        internal JavaPackageProjectEntry(string path, string label)
        {
            ProjectFile = path;
            DisplayName = label;
        }

        public string ProjectFile { get; private set; }
        public string DisplayName { get; private set; }
    }

    internal static class JavaPackageManagerSolutionProjects
    {
        internal static IList<JavaPackageProjectEntry> GetLoadedProjects(IServiceProvider services)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var solution = services == null ? null : services.GetService(typeof(SVsSolution)) as IVsSolution;
            if (solution == null) return new List<JavaPackageProjectEntry>();
            Guid anyProjectType = Guid.Empty;
            IEnumHierarchies enumerator;
            if (ErrorHandler.Failed(solution.GetProjectEnum(
                    (uint)__VSENUMPROJFLAGS.EPF_LOADEDINSOLUTION,
                    ref anyProjectType, out enumerator)) || enumerator == null)
                return new List<JavaPackageProjectEntry>();

            var paths = new List<string>();
            var buffer = new IVsHierarchy[1];
            uint fetched;
            while (enumerator.Next(1, buffer, out fetched) == VSConstants.S_OK && fetched == 1)
            {
                var hierarchy = buffer[0];
                if (hierarchy == null) continue;
                string path = null;
                try
                {
                    object ext;
                    if (ErrorHandler.Succeeded(hierarchy.GetProperty(VSConstants.VSITEMID_ROOT,
                        (int)__VSHPROPID.VSHPROPID_ExtObject, out ext)))
                    {
                        var project = ext as Project;
                        if (project != null) path = Normalize(project.FullName);
                    }
                }
                catch (COMException) { }
                catch (ArgumentException) { }
                if (path == null)
                {
                    try
                    {
                        string canonical;
                        if (ErrorHandler.Succeeded(hierarchy.GetCanonicalName(
                            VSConstants.VSITEMID_ROOT, out canonical)))
                            path = Normalize(canonical);
                    }
                    catch (COMException) { }
                    catch (ArgumentException) { }
                }
                if (path != null) paths.Add(path);
            }
            return CreateEntries(paths);
        }

        // Pure, deterministic model builder so duplicate names and path filtering
        // can be checked independently of the Visual Studio COM environment.
        internal static IList<JavaPackageProjectEntry> CreateEntries(IEnumerable<string> candidates)
        {
            var paths = (candidates ?? Enumerable.Empty<string>())
                .Select(Normalize)
                .Where(p => p != null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var duplicates = paths.GroupBy(p => Path.GetFileNameWithoutExtension(p),
                StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            return paths.Select(path =>
            {
                string name = Path.GetFileNameWithoutExtension(path);
                string label = duplicates[name] == 1 ? name : name + " — " + Path.GetDirectoryName(path);
                return new JavaPackageProjectEntry(path, label);
            }).ToList();
        }

        internal static string Normalize(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate) ||
                !candidate.EndsWith(".javaproj", StringComparison.OrdinalIgnoreCase) ||
                !Path.IsPathRooted(candidate)) return null;
            try
            {
                string path = Path.GetFullPath(candidate);
                return File.Exists(path) ? path : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                ex is ArgumentException || ex is NotSupportedException ||
                ex is System.Security.SecurityException) { return null; }
        }
    }
}
