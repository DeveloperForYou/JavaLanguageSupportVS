using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using JavaLanguageSupport.ProjectSystem.Packages;

namespace JavaLanguageSupport.ProjectSystem.Templates.Wizards
{
    /// <summary>
    /// F2E.3: package header for a destination already chosen by Visual Studio.
    /// This layer has no DTE, COM, UI or filesystem write dependencies.
    /// </summary>
    internal static class JavaTemplatePackageContext
    {
        public static bool TryGetHeader(string projectFile, string itemDirectory,
            out string header, out string packageName, out string error)
        {
            header = string.Empty;
            packageName = string.Empty;
            error = null;

            if (string.IsNullOrWhiteSpace(projectFile) ||
                !projectFile.EndsWith(".javaproj", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(itemDirectory))
            {
                error = "Progetto Java o directory di destinazione non disponibili.";
                return false;
            }

            string projectDirectory;
            try
            {
                projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectFile));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is IOException)
            {
                error = ex.Message;
                return false;
            }

            string buildHint = ReadBuildSystem(projectFile);
            JavaPackageInfo resolved;
            if (!JavaPackageResolver.TryResolveForDirectory(projectDirectory, itemDirectory,
                out resolved, out error, buildHint))
                return false;

            packageName = resolved.Name;
            header = resolved.GetDeclarationHeader("\r\n");
            return true;
        }

        /// <summary>
        /// Treat Maven/Gradle as authoritative for dependencies and source roots.
        /// The .javaproj hint only disambiguates projects containing both build files.
        /// </summary>
        private static string ReadBuildSystem(string projectFile)
        {
            try
            {
                var settings = new XmlReaderSettings
                {
                    XmlResolver = null,
                    DtdProcessing = DtdProcessing.Prohibit
                };
                using (var reader = XmlReader.Create(projectFile, settings))
                {
                    XDocument document = XDocument.Load(reader);
                    XElement element = document.Descendants()
                        .FirstOrDefault(node => node.Name.LocalName == "JavaBuildSystem");
                    if (element == null) return null;
                    string hint = element.Value.Trim();
                    return hint == "Maven" || hint == "Gradle" || hint == "Plain" || hint == "None"
                        ? hint : null;
                }
            }
            catch (XmlException) { return null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }
}
