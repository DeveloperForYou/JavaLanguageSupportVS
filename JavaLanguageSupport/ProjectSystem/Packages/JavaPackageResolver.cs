using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace JavaLanguageSupport.ProjectSystem.Packages
{
    /// <summary>
    /// A Java package derived from a file or directory within a known source root.
    /// This class only describes a target; it never creates or changes files.
    /// </summary>
    internal sealed class JavaPackageInfo
    {
        internal JavaPackageInfo(JavaSourceRootInfo sourceRoot, string directoryPath,
            string relativeDirectory, string name, bool isModuleDescriptor)
        {
            SourceRoot = sourceRoot;
            DirectoryPath = directoryPath;
            RelativeDirectory = relativeDirectory;
            Name = name;
            IsModuleDescriptor = isModuleDescriptor;
        }

        public JavaSourceRootInfo SourceRoot { get; private set; }
        public string DirectoryPath { get; private set; }
        // Relative to the source root; uses forward slashes, empty for default package.
        public string RelativeDirectory { get; private set; }
        public string Name { get; private set; }
        public bool IsDefaultPackage { get { return Name.Length == 0; } }
        public bool IsModuleDescriptor { get; private set; }
        public bool ShouldDeclarePackage { get { return !IsDefaultPackage && !IsModuleDescriptor; } }

        /// <summary>Complete Java package declaration without a newline, or empty.</summary>
        public string Declaration
        {
            get { return ShouldDeclarePackage ? "package " + Name + ";" : string.Empty; }
        }

        /// <summary>Declaration followed by two configurable line breaks, or empty.</summary>
        public string GetDeclarationHeader(string newline = "\r\n")
        {
            if (newline != "\n" && newline != "\r\n")
                throw new ArgumentException("Use LF or CRLF.", "newline");
            return ShouldDeclarePackage ? Declaration + newline + newline : string.Empty;
        }
    }

    /// <summary>
    /// F2E.2: converts the source-root-relative directory into a valid Java
    /// package. Uses the F2E.1 resolver, including Maven/Gradle/plain and
    /// explicitly configured roots; never guesses from the project root.
    /// No template/UI/filesystem side effects in this phase.
    /// </summary>
    internal static class JavaPackageResolver
    {
        private static readonly HashSet<string> ReservedIdentifiers =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "abstract", "assert", "boolean", "break", "byte", "case", "catch",
                "char", "class", "const", "continue", "default", "do", "double",
                "else", "enum", "extends", "final", "finally", "float", "for",
                "goto", "if", "implements", "import", "instanceof", "int",
                "interface", "long", "native", "new", "package", "private",
                "protected", "public", "return", "short", "static", "strictfp",
                "super", "switch", "synchronized", "this", "throw", "throws",
                "transient", "try", "void", "volatile", "while",
                "true", "false", "null", "_"
            };

        /// <summary>
        /// Resolve the package for a Java source file, which may not exist yet.
        /// Paths can be absolute or project-relative. No filesystem writes.
        /// </summary>
        public static bool TryResolveForFile(string projectDirectory, string javaFilePath,
            out JavaPackageInfo package, out string error,
            string buildSystemHint = null, IEnumerable<string> additionalRoots = null)
        {
            package = null;
            error = null;
            if (string.IsNullOrWhiteSpace(javaFilePath))
            {
                error = "Percorso del file Java mancante.";
                return false;
            }
            string fullFile;
            if (!TryNormalizeTarget(projectDirectory, javaFilePath, out fullFile, out error))
                return false;

            if (!string.Equals(Path.GetExtension(fullFile), ".java", StringComparison.OrdinalIgnoreCase))
            {
                error = "Il file non ha estensione .java.";
                return false;
            }

            string fileName = Path.GetFileName(fullFile);
            string directoryPath = Path.GetDirectoryName(fullFile);
            JavaPackageInfo fromDirectory;
            if (!TryResolveForDirectory(projectDirectory, directoryPath,
                out fromDirectory, out error, buildSystemHint, additionalRoots))
                return false;

            bool isModuleDescriptor = string.Equals(fileName, "module-info.java",
                StringComparison.OrdinalIgnoreCase);
            if (isModuleDescriptor && !fromDirectory.IsDefaultPackage)
            {
                error = "module-info.java deve trovarsi nella radice dei sorgenti Java.";
                return false;
            }

            package = new JavaPackageInfo(fromDirectory.SourceRoot,
                fromDirectory.DirectoryPath, fromDirectory.RelativeDirectory,
                fromDirectory.Name, isModuleDescriptor);
            return true;
        }

        /// <summary>
        /// Resolve a package for an existing or future directory. Never infers
        /// "file versus directory" from a dot in the name.
        /// </summary>
        public static bool TryResolveForDirectory(string projectDirectory, string directoryPath,
            out JavaPackageInfo package, out string error,
            string buildSystemHint = null, IEnumerable<string> additionalRoots = null)
        {
            package = null;
            error = null;
            string directory;
            if (!TryNormalizeTarget(projectDirectory, directoryPath, out directory, out error))
                return false;

            JavaSourceRootInfo root;
            if (!JavaSourceRootResolver.TryFindRootForPath(projectDirectory, directory,
                out root, buildSystemHint, additionalRoots))
            {
                error = "Directory esterna alle source root Java del progetto.";
                return false;
            }

            string relative = directory.Substring(root.FullPath.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace(Path.DirectorySeparatorChar, '/');
            string packageName = relative.Replace('/', '.');
            string invalidSegment;
            if (!TryValidatePackageName(packageName, out invalidSegment))
            {
                error = "Segmento package Java non valido: " + invalidSegment;
                return false;
            }

            package = new JavaPackageInfo(root, directory, relative, packageName, false);
            return true;
        }

        /// <summary>
        /// Validates segments for use in a Java package declaration. Empty
        /// means the default package. Rejects Java reserved words and '_'.
        /// This deliberately does not try to evaluate build-tool variables.
        /// </summary>
        public static bool TryValidatePackageName(string packageName, out string invalidSegment)
        {
            invalidSegment = null;
            if (packageName == null)
            {
                invalidSegment = "<null>";
                return false;
            }
            if (packageName.Length == 0)
                return true;

            string[] segments = packageName.Split('.');
            foreach (string segment in segments)
            {
                if (!IsValidIdentifier(segment))
                {
                    invalidSegment = segment.Length == 0 ? "<vuoto>" : segment;
                    return false;
                }
            }
            return true;
        }

        private static bool IsValidIdentifier(string identifier)
        {
            if (string.IsNullOrEmpty(identifier) || ReservedIdentifiers.Contains(identifier))
                return false;

            for (int offset = 0; offset < identifier.Length;)
            {
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(identifier, offset);
                bool first = offset == 0;
                if (!IsJavaIdentifierCategory(category, first))
                    return false;

                if (char.IsHighSurrogate(identifier[offset]) &&
                    offset + 1 < identifier.Length && char.IsLowSurrogate(identifier[offset + 1]))
                    offset += 2;
                else
                    offset++;
            }
            return true;
        }

        private static bool IsJavaIdentifierCategory(UnicodeCategory category, bool first)
        {
            switch (category)
            {
                case UnicodeCategory.UppercaseLetter:
                case UnicodeCategory.LowercaseLetter:
                case UnicodeCategory.TitlecaseLetter:
                case UnicodeCategory.ModifierLetter:
                case UnicodeCategory.OtherLetter:
                case UnicodeCategory.LetterNumber:
                case UnicodeCategory.CurrencySymbol:
                case UnicodeCategory.ConnectorPunctuation:
                    return true;
                case UnicodeCategory.DecimalDigitNumber:
                case UnicodeCategory.NonSpacingMark:
                case UnicodeCategory.SpacingCombiningMark:
                case UnicodeCategory.Format:
                    return !first;
                default:
                    return false;
            }
        }

        private static bool TryNormalizeTarget(string projectDirectory, string target,
            out string fullPath, out string error)
        {
            fullPath = null;
            error = null;
            if (string.IsNullOrWhiteSpace(projectDirectory) || string.IsNullOrWhiteSpace(target))
            {
                error = "Percorso del progetto o della destinazione mancante.";
                return false;
            }
            try
            {
                string baseDirectory = Path.GetFullPath(projectDirectory);
                fullPath = Path.GetFullPath(Path.IsPathRooted(target)
                    ? target : Path.Combine(baseDirectory, target));
                return true;
            }
            catch (ArgumentException ex) { error = ex.Message; }
            catch (NotSupportedException ex) { error = ex.Message; }
            catch (IOException ex) { error = ex.Message; }
            return false;
        }
    }
}
