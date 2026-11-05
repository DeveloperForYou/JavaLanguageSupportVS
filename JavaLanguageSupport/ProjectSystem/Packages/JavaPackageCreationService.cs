using System;
using System.IO;
using System.Linq;

namespace JavaLanguageSupport.ProjectSystem.Packages
{
    /// <summary>
    /// F2F.1: pure planning for the New Java Package command. Never writes files.
    /// Package names typed here are relative to the selected folder, or are
    /// complete names when the project node is selected.
    /// </summary>
    internal sealed class JavaPackageCreationPlan
    {
        internal JavaPackageCreationPlan(string projectDirectory, string baseDirectory,
            string sourceRootDirectory, string targetDirectory,
            string fullPackageName, string relativePackageName)
        {
            ProjectDirectory = projectDirectory;
            BaseDirectory = baseDirectory;
            SourceRootDirectory = sourceRootDirectory;
            TargetDirectory = targetDirectory;
            FullPackageName = fullPackageName;
            RelativePackageName = relativePackageName;
        }

        public string ProjectDirectory { get; private set; }
        public string BaseDirectory { get; private set; }
        public string SourceRootDirectory { get; private set; }
        public string TargetDirectory { get; private set; }
        public string FullPackageName { get; private set; }
        public string RelativePackageName { get; private set; }
    }

    internal static class JavaPackageCreationService
    {
        public static bool TryGetBase(string projectDirectory, string selectedDirectory,
            bool projectNodeSelected, string buildHint, out string baseDirectory,
            out string sourceRoot, out string existingPackage, out string error)
        {
            baseDirectory = null;
            sourceRoot = null;
            existingPackage = null;
            error = null;
            if (string.IsNullOrWhiteSpace(projectDirectory) ||
                string.IsNullOrWhiteSpace(selectedDirectory))
            {
                error = "Percorso del progetto o cartella selezionata mancante.";
                return false;
            }
            try
            {
                string project = Path.GetFullPath(projectDirectory);
                string selected = Path.GetFullPath(selectedDirectory);
                if (!Directory.Exists(project) || !IsSameOrChild(project, selected))
                {
                    error = "La selezione non appartiene alla directory del progetto.";
                    return false;
                }

                if (projectNodeSelected)
                {
                    var roots = JavaSourceRootResolver.Discover(project, buildHint);
                    var main = roots.FirstOrDefault(r => r.Kind == JavaSourceRootKind.Main)
                        ?? roots.FirstOrDefault(r => r.Kind == JavaSourceRootKind.Plain)
                        ?? roots.FirstOrDefault();
                    if (main == null)
                    {
                        error = "Non e' stata individuata una source root Java.";
                        return false;
                    }
                    baseDirectory = main.FullPath;
                    sourceRoot = main.FullPath;
                    existingPackage = string.Empty;
                    return true;
                }

                if (!Directory.Exists(selected))
                {
                    error = "La cartella selezionata non esiste.";
                    return false;
                }
                JavaPackageInfo sourcePackage;
                if (!JavaPackageResolver.TryResolveForDirectory(project, selected,
                    out sourcePackage, out error, buildHint))
                {
                    return false;
                }
                baseDirectory = sourcePackage.DirectoryPath;
                sourceRoot = sourcePackage.SourceRoot.FullPath;
                existingPackage = sourcePackage.Name;
                return true;
            }
            catch (ArgumentException ex) { error = ex.Message; return false; }
            catch (IOException ex) { error = ex.Message; return false; }
            catch (NotSupportedException ex) { error = ex.Message; return false; }
            catch (UnauthorizedAccessException ex) { error = ex.Message; return false; }
        }

        public static bool TryPlan(string projectDirectory, string selectedDirectory,
            bool projectNodeSelected, string relativePackageName, string buildHint,
            out JavaPackageCreationPlan plan, out string error)
        {
            plan = null;
            error = null;
            string baseDirectory, sourceRoot, existingPackage;
            if (!TryGetBase(projectDirectory, selectedDirectory, projectNodeSelected,
                buildHint, out baseDirectory, out sourceRoot, out existingPackage,
                out error)) return false;

            string suffix = relativePackageName == null ? string.Empty : relativePackageName.Trim();
            string invalidSegment = "";
            if (suffix.Length == 0 ||
                !JavaPackageResolver.TryValidatePackageName(suffix, out invalidSegment))
            {
                error = suffix.Length == 0
                    ? "Inserire un nome per il nuovo package."
                    : "Nome package Java non valido: " + invalidSegment;
                return false;
            }
            foreach (string segment in suffix.Split('.'))
            {
                // Some valid Java identifiers are invalid Windows folder names.
                string upper = segment.ToUpperInvariant();
                if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    IsWindowsReservedName(upper) || segment.EndsWith(" ", StringComparison.Ordinal))
                {
                    error = "Nome cartella non consentito da Windows: " + segment;
                    return false;
                }
            }

            try
            {
                string result = Path.GetFullPath(Path.Combine(baseDirectory,
                    suffix.Replace('.', Path.DirectorySeparatorChar)));
                if (!IsSameOrChild(sourceRoot, result) ||
                    string.Equals(sourceRoot, result, StringComparison.OrdinalIgnoreCase))
                {
                    error = "La destinazione e' fuori dalla source root.";
                    return false;
                }
                JavaPackageInfo resolved;
                if (!JavaPackageResolver.TryResolveForDirectory(projectDirectory, result,
                    out resolved, out error, buildHint)) return false;
                if (!string.Equals(resolved.SourceRoot.FullPath, sourceRoot,
                    StringComparison.OrdinalIgnoreCase))
                {
                    error = "La destinazione appartiene a una source root differente.";
                    return false;
                }
                if (Directory.Exists(result))
                {
                    error = "Il package esiste gia': " + resolved.Name;
                    return false;
                }
                plan = new JavaPackageCreationPlan(
                    Path.GetFullPath(projectDirectory), baseDirectory, sourceRoot,
                    result, resolved.Name, suffix);
                return true;
            }
            catch (ArgumentException ex) { error = ex.Message; return false; }
            catch (IOException ex) { error = ex.Message; return false; }
            catch (NotSupportedException ex) { error = ex.Message; return false; }
            catch (UnauthorizedAccessException ex) { error = ex.Message; return false; }
        }

        private static bool IsWindowsReservedName(string name)
        {
            if (name == "CON" || name == "PRN" || name == "AUX" || name == "NUL")
                return true;
            if (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) ||
                name.StartsWith("LPT", StringComparison.Ordinal)) &&
                name[3] >= '1' && name[3] <= '9') return true;
            return false;
        }

        private static bool IsSameOrChild(string parentPath, string childPath)
        {
            string parent = parentPath.TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            string child = childPath.TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            return string.Equals(parent, child, StringComparison.OrdinalIgnoreCase) ||
                child.StartsWith(parent + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
        }
    }
}
