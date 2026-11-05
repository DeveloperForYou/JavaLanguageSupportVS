using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace JavaLanguageSupport.ProjectSystem.Packages
{
    /// <summary>
    /// F2F.1: registra un package vuoto come elemento MSBuild Folder del progetto CPS.
    /// Non usa ProjectItems.AddFolder (che può fallire nel CPS con InternalErrorException).
    /// </summary>
    internal static class JavaEmptyPackageProjectItems
    {
        public static bool TryInclude(string projectFile, string folder, out string error)
        {
            error = null;
            try
            {
                if (string.IsNullOrWhiteSpace(projectFile) ||
                    !projectFile.EndsWith(".javaproj", StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(projectFile))
                {
                    error = "Il file di progetto .javaproj non è disponibile.";
                    return false;
                }
                var fullProject = Path.GetFullPath(projectFile);
                var projectDirectory = Path.GetDirectoryName(fullProject);
                var fullFolder = Path.GetFullPath(folder).TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var projectPrefix = projectDirectory.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
                if (!fullFolder.StartsWith(projectPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    error = "La cartella non si trova all'interno del progetto Java.";
                    return false;
                }
                if (!Directory.Exists(fullFolder))
                {
                    error = "La cartella del package non esiste sul disco.";
                    return false;
                }

                var relative = fullFolder.Substring(projectPrefix.Length)
                    .Replace('/', '\\').TrimEnd('\\') + "\\";
                // Il resolver F2F.1 valida già i segmenti; qui difendiamo
                // anche i percorsi MSBuild esterni, eventualmente interpolati.
                if (relative.Contains("$(") || relative.Contains("@(") ||
                    relative.IndexOf(';') >= 0 || relative.IndexOf('*') >= 0 ||
                    relative.IndexOf('?') >= 0 || relative.StartsWith("..", StringComparison.Ordinal))
                {
                    error = "Percorso relativo non valido per Folder Include.";
                    return false;
                }

                byte[] before = File.ReadAllBytes(fullProject);
                if (before.Length >= 2 &&
                    ((before[0] == 0xFF && before[1] == 0xFE) ||
                     (before[0] == 0xFE && before[1] == 0xFF)))
                {
                    error = "Codifica UTF-16 non supportata: nessuna modifica al progetto.";
                    return false;
                }
                bool bom = before.Length >= 3 && before[0] == 0xEF &&
                           before[1] == 0xBB && before[2] == 0xBF;
                var encoding = new UTF8Encoding(bom, true);
                string xml = encoding.GetString(before, bom ? 3 : 0,
                    before.Length - (bom ? 3 : 0));
                XDocument doc = ReadProject(xml);
                if (doc.Root == null || doc.Root.Name.LocalName != "Project")
                {
                    error = "Il documento non è un progetto MSBuild.";
                    return false;
                }
                string normalized = NormalizeItemPath(relative);
                if (doc.Descendants().Any(e => e.Name.LocalName == "Folder" &&
                    string.Equals(NormalizeItemPath((string)e.Attribute("Include")),
                        normalized, StringComparison.OrdinalIgnoreCase)))
                    return true;

                // Inserimento minimo: conserva il resto del .javaproj e i CRLF.
                int end = xml.LastIndexOf("</Project>", StringComparison.Ordinal);
                if (end < 0 || xml.Substring(end + "</Project>".Length).Trim().Length != 0)
                {
                    error = "Chiusura </Project> non riconosciuta: progetto non modificato.";
                    return false;
                }
                string newline = xml.Contains("\r\n") ? "\r\n" : "\n";
                string prefix = (end > 0 && xml[end - 1] != '\n') ? newline : string.Empty;
                string include = System.Security.SecurityElement.Escape(relative);
                string insertion = prefix + "  <ItemGroup>" + newline +
                    "    <Folder Include=\"" + include + "\" />" + newline +
                    "  </ItemGroup>" + newline;
                string updated = xml.Substring(0, end) + insertion + xml.Substring(end);
                // Convalida l'XML prima della scrittura.
                ReadProject(updated);
                // Non sovrascrivere modifiche concorrenti del progetto.
                if (!before.SequenceEqual(File.ReadAllBytes(fullProject)))
                {
                    error = "Il progetto è stato modificato mentre il package veniva creato.";
                    return false;
                }
                File.WriteAllText(fullProject, updated, encoding);
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException ||
                ex is UnauthorizedAccessException || ex is XmlException ||
                ex is System.Text.DecoderFallbackException || ex is NotSupportedException)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string NormalizeItemPath(string item)
        {
            return (item ?? string.Empty).Replace('/', '\\').Trim().TrimEnd('\\');
        }

        private static XDocument ReadProject(string xml)
        {
            var settings = new XmlReaderSettings {
                XmlResolver = null, DtdProcessing = DtdProcessing.Prohibit
            };
            using (var reader = XmlReader.Create(new StringReader(xml), settings))
            {
                return XDocument.Load(reader);
            }
        }
    }
}
