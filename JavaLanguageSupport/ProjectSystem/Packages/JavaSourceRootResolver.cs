using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace JavaLanguageSupport.ProjectSystem.Packages
{
    // F2E.1: metadata of a source root. No VS services, files, or projects are changed.
    internal enum JavaBuildSystemKind
    {
        Maven,
        Gradle,
        Plain
    }

    internal enum JavaSourceRootKind
    {
        Main,
        Test,
        Additional,
        Plain
    }

    internal sealed class JavaSourceRootInfo
    {
        internal JavaSourceRootInfo(string fullPath, string relativePath,
            JavaBuildSystemKind buildSystem, JavaSourceRootKind kind, string origin)
        {
            FullPath = fullPath;
            RelativePath = relativePath;
            BuildSystem = buildSystem;
            Kind = kind;
            Origin = origin;
            Exists = Directory.Exists(fullPath);
        }

        public string FullPath { get; private set; }
        public string RelativePath { get; private set; }
        public JavaBuildSystemKind BuildSystem { get; private set; }
        public JavaSourceRootKind Kind { get; private set; }
        public string Origin { get; private set; }
        public bool Exists { get; private set; }
    }

    /// <summary>
    /// F2E.1: discovers Java source roots without executing Maven/Gradle, modifying
    /// the project, or making a global Java-file association.
    /// F2E.2 will use the longest matching root to derive package names.
    /// </summary>
    internal static class JavaSourceRootResolver
    {
        /// <summary>
        /// Returns conventional roots even when they do not exist (for future
        /// package creation), plus existing Gradle source sets and explicit roots.
        /// The optional hint is "Maven", "Gradle", or "Plain". If not supplied,
        /// build files are detected in the project directory.
        /// </summary>
        public static IReadOnlyList<JavaSourceRootInfo> Discover(
            string projectDirectory,
            string buildSystemHint = null,
            IEnumerable<string> additionalRoots = null)
        {
            string baseDirectory = NormalizeProjectDirectory(projectDirectory);
            JavaBuildSystemKind buildSystem = DetectBuildSystem(baseDirectory, buildSystemHint);
            var result = new List<JavaSourceRootInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (buildSystem == JavaBuildSystemKind.Maven)
            {
                string main = ReadMavenSourceDirectory(baseDirectory, "sourceDirectory") ?? "src/main/java";
                string test = ReadMavenSourceDirectory(baseDirectory, "testSourceDirectory") ?? "src/test/java";
                AddRoot(baseDirectory, main, buildSystem, JavaSourceRootKind.Main,
                    main == "src/main/java" ? "MavenConvention" : "MavenPom", result, seen);
                AddRoot(baseDirectory, test, buildSystem, JavaSourceRootKind.Test,
                    test == "src/test/java" ? "MavenConvention" : "MavenPom", result, seen);
            }
            else if (buildSystem == JavaBuildSystemKind.Gradle)
            {
                AddRoot(baseDirectory, "src/main/java", buildSystem, JavaSourceRootKind.Main,
                    "GradleConvention", result, seen);
                AddRoot(baseDirectory, "src/test/java", buildSystem, JavaSourceRootKind.Test,
                    "GradleConvention", result, seen);

                // Gradle may have source sets such as src/integrationTest/java.
                // Only add these when the directory really exists; a custom
                // sourceSets { java.srcDirs = ... } needs an explicit override.
                string src = Path.Combine(baseDirectory, "src");
                if (Directory.Exists(src))
                {
                    foreach (string sourceSet in Directory.GetDirectories(src)
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                    {
                        string setName = Path.GetFileName(sourceSet);
                        if (string.Equals(setName, "main", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(setName, "test", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string javaDirectory = Path.Combine(sourceSet, "java");
                        if (Directory.Exists(javaDirectory))
                        {
                            AddRoot(baseDirectory, Path.Combine("src", setName, "java"),
                                buildSystem, JavaSourceRootKind.Additional, "GradleSourceSet",
                                result, seen);
                        }
                    }
                }
            }
            else
            {
                // Plain Java projects commonly use src/; do not claim that
                // arbitrary project subfolders are package roots.
                AddRoot(baseDirectory, "src", buildSystem, JavaSourceRootKind.Plain,
                    "PlainConvention", result, seen);
                if (Directory.Exists(Path.Combine(baseDirectory, "src", "main", "java")))
                {
                    AddRoot(baseDirectory, "src/main/java", buildSystem,
                        JavaSourceRootKind.Main, "ExistingDirectory", result, seen);
                }
                if (Directory.Exists(Path.Combine(baseDirectory, "src", "test", "java")))
                {
                    AddRoot(baseDirectory, "src/test/java", buildSystem,
                        JavaSourceRootKind.Test, "ExistingDirectory", result, seen);
                }
            }

            if (additionalRoots != null)
            {
                foreach (string configuredRoot in additionalRoots)
                {
                    AddRoot(baseDirectory, configuredRoot, buildSystem,
                        JavaSourceRootKind.Additional, "Explicit", result, seen);
                }
            }

            return result.AsReadOnly();
        }

        /// <summary>
        /// Finds the most specific source root containing a file or directory.
        /// A directory named 'src/main/java2' will not match 'src/main/java'.
        /// Paths outside the project are not considered source roots.
        /// </summary>
        public static bool TryFindRootForPath(
            string projectDirectory,
            string fileOrDirectoryPath,
            out JavaSourceRootInfo root,
            string buildSystemHint = null,
            IEnumerable<string> additionalRoots = null)
        {
            root = null;
            if (string.IsNullOrWhiteSpace(fileOrDirectoryPath))
            {
                return false;
            }

            string baseDirectory = NormalizeProjectDirectory(projectDirectory);
            string target;
            try
            {
                target = Path.GetFullPath(Path.IsPathRooted(fileOrDirectoryPath)
                    ? fileOrDirectoryPath
                    : Path.Combine(baseDirectory, fileOrDirectoryPath));
            }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (IOException) { return false; }

            foreach (JavaSourceRootInfo candidate in Discover(baseDirectory, buildSystemHint, additionalRoots)
                .OrderByDescending(item => item.FullPath.Length))
            {
                if (IsSameOrDescendant(candidate.FullPath, target))
                {
                    root = candidate;
                    return true;
                }
            }

            return false;
        }

        public static JavaBuildSystemKind DetectBuildSystem(string projectDirectory,
            string buildSystemHint = null)
        {
            string baseDirectory = NormalizeProjectDirectory(projectDirectory);
            if (!string.IsNullOrWhiteSpace(buildSystemHint))
            {
                if (buildSystemHint.Equals("Maven", StringComparison.OrdinalIgnoreCase))
                    return JavaBuildSystemKind.Maven;
                if (buildSystemHint.Equals("Gradle", StringComparison.OrdinalIgnoreCase))
                    return JavaBuildSystemKind.Gradle;
                if (buildSystemHint.Equals("Plain", StringComparison.OrdinalIgnoreCase) ||
                    buildSystemHint.Equals("None", StringComparison.OrdinalIgnoreCase))
                    return JavaBuildSystemKind.Plain;
            }

            // Prefer the POM for an ambiguous dual-build project. Callers can
            // supply the explicit JavaBuildSystem hint from the .javaproj.
            if (File.Exists(Path.Combine(baseDirectory, "pom.xml")))
                return JavaBuildSystemKind.Maven;

            if (File.Exists(Path.Combine(baseDirectory, "build.gradle")) ||
                File.Exists(Path.Combine(baseDirectory, "build.gradle.kts")) ||
                File.Exists(Path.Combine(baseDirectory, "settings.gradle")) ||
                File.Exists(Path.Combine(baseDirectory, "settings.gradle.kts")))
                return JavaBuildSystemKind.Gradle;

            return JavaBuildSystemKind.Plain;
        }

        private static string NormalizeProjectDirectory(string projectDirectory)
        {
            if (string.IsNullOrWhiteSpace(projectDirectory))
                throw new ArgumentException("Project directory must not be empty.", "projectDirectory");
            return Path.GetFullPath(projectDirectory).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static bool IsSameOrDescendant(string parent, string child)
        {
            return string.Equals(parent, child, StringComparison.OrdinalIgnoreCase) ||
                child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static void AddRoot(string baseDirectory, string relativeOrAbsolute,
            JavaBuildSystemKind buildSystem, JavaSourceRootKind kind,
            string origin, List<JavaSourceRootInfo> result, HashSet<string> seen)
        {
            if (string.IsNullOrWhiteSpace(relativeOrAbsolute))
                return;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(Path.IsPathRooted(relativeOrAbsolute)
                    ? relativeOrAbsolute
                    : Path.Combine(baseDirectory, relativeOrAbsolute));
            }
            catch (ArgumentException) { return; }
            catch (NotSupportedException) { return; }
            catch (IOException) { return; }

            // Never return an outside-the-project path as a source root.
            if (!IsSameOrDescendant(baseDirectory, fullPath) ||
                string.Equals(baseDirectory, fullPath, StringComparison.OrdinalIgnoreCase) ||
                !seen.Add(fullPath))
            {
                return;
            }

            string relativePath = fullPath.Substring(baseDirectory.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace(Path.DirectorySeparatorChar, '/');
            result.Add(new JavaSourceRootInfo(fullPath, relativePath,
                buildSystem, kind, origin));
        }

        private static string ReadMavenSourceDirectory(string baseDirectory, string elementName)
        {
            string pom = Path.Combine(baseDirectory, "pom.xml");
            if (!File.Exists(pom))
                return null;

            try
            {
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                };
                using (XmlReader reader = XmlReader.Create(pom, settings))
                {
                    XDocument document = XDocument.Load(reader);
                    if (document.Root == null)
                        return null;

                    XNamespace ns = document.Root.Name.Namespace;
                    XElement build = document.Root.Element(ns + "build");
                    XElement element = build == null ? null : build.Element(ns + elementName);
                    if (element == null || string.IsNullOrWhiteSpace(element.Value))
                        return null;

                    // These two Maven variables can be expanded without running
                    // Maven; other ${...} expressions are not evaluated here.
                    string value = element.Value.Trim()
                        .Replace("${project.basedir}", baseDirectory)
                        .Replace("${basedir}", baseDirectory);
                    if (value.Contains("${"))
                        return null;

                    string full = Path.GetFullPath(Path.IsPathRooted(value)
                        ? value : Path.Combine(baseDirectory, value));
                    return IsSameOrDescendant(baseDirectory, full) &&
                        !string.Equals(full, baseDirectory, StringComparison.OrdinalIgnoreCase)
                        ? full : null;
                }
            }
            // A malformed/unreadable POM must not prevent opening a Java project.
            catch (XmlException) { return null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
        }
    }
}
