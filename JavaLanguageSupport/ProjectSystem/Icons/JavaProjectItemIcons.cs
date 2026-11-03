// F2D-ITEM-ICONS-OWNED: personalizza soltanto i file del progetto Java CPS.
using System;
using System.IO;
using Microsoft.VisualStudio.Imaging.Interop;

namespace JavaLanguageSupport.ProjectSystem.Icons
{
    /// <summary>
    /// Identificazione per nome/estensione, solo in progetti JavaLanguageSupportCps.
    /// I moniker sono dichiarati nel JavaProject.imagemanifest gia registrato in F2C.
    /// Per i file non riconosciuti si conserva l'icona nativa di Visual Studio.
    /// </summary>
    internal static class JavaProjectItemIcons
    {
        private static readonly Guid ManifestGuid =
            new Guid("d203fa12-c9a4-47a1-8f62-39afba8d2fa0");

        public static bool TryGetMoniker(string itemName, out ImageMoniker moniker)
        {
            moniker = default(ImageMoniker);
            if (string.IsNullOrWhiteSpace(itemName)) return false;
            string file = Path.GetFileName(itemName).ToLowerInvariant();
            int id;

            // Priorita: nome esatto, poi estensioni specifiche, poi estensioni generiche.
            switch (file)
            {
                case "pom.xml": id = 12; break;
                case "mvnw":
                case "mvnw.cmd":
                case "mvnw.bat":
                case "maven-wrapper.jar":
                case "maven-wrapper.properties":
                    id = 13; break;
                case "gradlew":
                case "gradlew.bat":
                case "gradle-wrapper.jar":
                case "gradle-wrapper.properties":
                    id = 16; break;
                case "gradle.properties":
                case "gradle.lockfile":
                    id = 14; break;
                case ".classpath":
                case ".project":
                case ".factorypath":
                    id = 19; break;
                case "manifest.mf": id = 21; break;
                case ".gitignore":
                case ".gitattributes":
                case ".gitmodules": id = 23; break;
                case ".editorconfig": id = 28; break;
                case "dockerfile":
                case ".dockerignore": id = 30; break;
                case "checkstyle.xml":
                case "pmd.xml":
                case "spotbugs-exclude.xml":
                case "logback.xml":
                case "log4j2.xml":
                case "hibernate.cfg.xml":
                case "web.xml":
                case "settings.xml":
                    id = 27; break;
                default:
                    if (file.EndsWith(".gradle.kts", StringComparison.Ordinal)) id = 15;
                    else if (file.EndsWith(".gradle", StringComparison.Ordinal)) id = 14;
                    else if (file.EndsWith(".java", StringComparison.Ordinal))
                    {
                        string typeName = file.Substring(0, file.Length - 5);
                        // Distingue i test in stile JUnit senza modificare sorgenti/progetti.
                        id = typeName.EndsWith("test", StringComparison.Ordinal) ||
                             typeName.EndsWith("tests", StringComparison.Ordinal) ||
                             Path.GetFileNameWithoutExtension(itemName).EndsWith("IT", StringComparison.Ordinal) ||
                             typeName.StartsWith("test", StringComparison.Ordinal)
                             ? 11 : 10;
                    }
                    else if (file.EndsWith(".kt", StringComparison.Ordinal) ||
                             file.EndsWith(".kts", StringComparison.Ordinal)) id = 24;
                    else if (file.EndsWith(".groovy", StringComparison.Ordinal)) id = 25;
                    else if (file.EndsWith(".class", StringComparison.Ordinal)) id = 26;
                    else if (file.EndsWith(".jar", StringComparison.Ordinal) ||
                             file.EndsWith(".war", StringComparison.Ordinal) ||
                             file.EndsWith(".ear", StringComparison.Ordinal)) id = 20;
                    else if (file.EndsWith(".properties", StringComparison.Ordinal) ||
                             file == ".env" || file.StartsWith(".env.", StringComparison.Ordinal)) id = 17;
                    else if (file.EndsWith(".yaml", StringComparison.Ordinal) ||
                             file.EndsWith(".yml", StringComparison.Ordinal)) id = 18;
                    else if (file.EndsWith(".toml", StringComparison.Ordinal)) id = 29;
                    else if (file.EndsWith(".iml", StringComparison.Ordinal)) id = 19;
                    else if (file.StartsWith("dockerfile.", StringComparison.Ordinal)) id = 30;
                    else if (file.StartsWith("readme", StringComparison.Ordinal) ||
                             file.StartsWith("changelog", StringComparison.Ordinal) ||
                             file.StartsWith("license", StringComparison.Ordinal) ||
                             file.StartsWith("notice", StringComparison.Ordinal)) id = 22;
                    else return false;
                    break;
            }

            moniker = new ImageMoniker { Guid = ManifestGuid, Id = id };
            return true;
        }
    }
}
