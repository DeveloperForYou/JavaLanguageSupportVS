using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace JavaLanguageSupport.ProjectSystem.PackageManager
{
    // JPM-S2A-CONFLICT-GUARD-20261124
    // Read-only comparison of already referenced and newly resolved Plain JARs.
    // Never silently select a different binary: resolving the full dependency
    // graph across direct roots requires explicit mediation in JPM-S2B.
    internal static class JavaPlainVersionConflictGuard
    {
        // JPM-S2C-20261124: expose the exact same verified identities used by S2A.
        // Data is a snapshot only; no mutation or version mediation occurs here.
        internal sealed class Library
        {
            internal string Identity;
            internal string Version;
            internal string Owner;
            internal string Scope;
            internal string Name;
        }

        internal sealed class Conflict
        {
            internal string Identity;
            internal List<string> Versions;
            internal List<Library> Libraries;
        }

        internal sealed class Report
        {
            internal int ConflictCount { get; set; }
            internal int UnverifiedCount { get; set; }
            internal string Details { get; set; }
            internal List<Conflict> Conflicts { get; set; }
            internal List<Library> Libraries { get; set; }
        }

        // Preserve InvalidOperationException contract used by S2B logging, but
        // expose a structured snapshot of the rejected candidate to the UI.
        internal sealed class CandidateConflictException : InvalidOperationException
        {
            internal Report Candidate { get; private set; }
            internal CandidateConflictException(Report candidate, string message)
                : base(message) { Candidate = candidate; }
        }

        private sealed class Entry
        {
            internal string Identity;
            internal string Version;
            internal string Owner;
            internal string Scope;
            internal string Name;
        }

        private static readonly char[] InvalidLiteral = { '*', '?', '$', '%', ';', '\r', '\n' };

        // Called only during Prepare/BuildPlan, before any JAR or .javaproj commit.
        // On Update/ChangeScope the replaced direct dependency is excluded from
        // the comparison. Remove remains possible even in a conflicted project.
        internal static void VerifyCandidate(string projectFile, string replacedOwner,
            string stage, IEnumerable<string> names)
        {
            var entries = LoadManaged(projectFile, replacedOwner, out int unknown);
            foreach (string name in names)
            {
                string path = Path.Combine(stage, name);
                Entry entry = Identify(path, replacedOwner + " (nuova risoluzione)",
                    "selezionato");
                if (entry == null) { unknown++; continue; }
                entries.Add(entry);
            }
            Report report = Summarize(entries, unknown);
            if (report.ConflictCount != 0)
                throw new CandidateConflictException(report,
                    "Conflitto tra versioni transitive Plain: installazione non applicata.\n\n" +
                    report.Details + "\n\nAllineare le versioni delle dipendenze " +
                    "dirette oppure modificare la selezione, poi riprovare. " +
                    "Il JPM non esclude automaticamente un JAR necessario a un altro pacchetto.");
        }

        // Explicit UI audit: report existing conflicts without modifying anything.
        internal static Report Audit(string projectFile)
        {
            int unknown;
            var entries = LoadManaged(projectFile, null, out unknown);
            return Summarize(entries, unknown);
        }

        private static List<Entry> LoadManaged(string projectFile, string ownerToExclude,
            out int unknown)
        {
            unknown = 0;
            if (string.IsNullOrWhiteSpace(projectFile) || !File.Exists(projectFile))
                throw new IOException("Progetto .javaproj non disponibile.");
            var settings = new XmlReaderSettings {
                XmlResolver = null, DtdProcessing = DtdProcessing.Prohibit,
                MaxCharactersInDocument = 8 * 1024 * 1024
            };
            var xml = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(projectFile, settings)) xml.Load(reader);
            var project = xml.DocumentElement;
            if (project == null || project.LocalName != "Project")
                throw new InvalidDataException("Il progetto non contiene un elemento Project valido.");

            string root = Path.GetDirectoryName(Path.GetFullPath(projectFile));
            string managedRoot = Path.Combine(root, "lib", "java-packages")
                .TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var entries = new List<Entry>();
            foreach (var group in project.ChildNodes.OfType<XmlElement>()
                .Where(x => x.LocalName == "ItemGroup"))
            foreach (var item in group.ChildNodes.OfType<XmlElement>()
                .Where(x => x.LocalName == "JavaJarReference"))
            {
                string owner = ReadChild(item, JavaPlainPackageManager.ManagedId);
                if (string.IsNullOrWhiteSpace(owner) ||
                    string.Equals(owner, ownerToExclude, StringComparison.Ordinal)) continue;
                string include = item.GetAttribute("Include");
                if (string.IsNullOrWhiteSpace(include) || include.IndexOfAny(InvalidLiteral) >= 0 ||
                    !include.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                { unknown++; continue; }
                string full;
                try {
                    full = Path.GetFullPath(Path.IsPathRooted(include) ? include :
                        Path.Combine(root, include));
                    if (!full.StartsWith(managedRoot, StringComparison.OrdinalIgnoreCase) ||
                        !File.Exists(full) ||
                        (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                    { unknown++; continue; }
                    JavaPlainPackageChange.EnsureDirectorySafe(root, Path.GetDirectoryName(full));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                    ex is ArgumentException || ex is NotSupportedException ||
                    ex is PathTooLongException)
                { unknown++; continue; }
                string scope = ReadChild(item, JavaPackageScopes.PlainMetadata);
                Entry entry;
                try { entry = Identify(full, owner, string.IsNullOrWhiteSpace(scope) ? "compile" : scope); }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException ||
                    ex is UnauthorizedAccessException)
                { unknown++; continue; }
                if (entry == null) { unknown++; continue; }
                entries.Add(entry);
            }
            return entries;
        }

        private static string ReadChild(XmlElement item, string name)
        {
            var child = item.ChildNodes.OfType<XmlElement>()
                .FirstOrDefault(x => x.LocalName == name);
            return child == null ? null : child.InnerText.Trim();
        }

        private static Entry Identify(string path, string owner, string scope)
        {
            string name = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(name) ||
                !name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) return null;
            string stem = name.Substring(0, name.Length - 4);
            string identity = null, version = null;
            // Maven's copy-dependencies -Dmdep.prependGroupId=true creates
            // group.artifact-version.jar. Match embedded Maven metadata against
            // that filename to avoid trusting a shaded/foreign pom.properties.
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                foreach (var prop in zip.Entries)
                {
                    if (!prop.FullName.StartsWith("META-INF/maven/", StringComparison.OrdinalIgnoreCase) ||
                        !prop.FullName.EndsWith("/pom.properties", StringComparison.OrdinalIgnoreCase) ||
                        prop.Length <= 0 || prop.Length > 8192) continue;
                    var properties = new Dictionary<string, string>(StringComparer.Ordinal);
                    using (var reader = new StreamReader(prop.Open(), Encoding.UTF8, true))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            int separator = line.IndexOf('=');
                            if (separator <= 0) continue;
                            properties[line.Substring(0, separator).Trim()] =
                                line.Substring(separator + 1).Trim();
                        }
                    }
                    string g, a, v;
                    if (!properties.TryGetValue("groupId", out g) ||
                        !properties.TryGetValue("artifactId", out a) ||
                        !properties.TryGetValue("version", out v)) continue;
                    if (g.Length == 0 || a.Length == 0 || v.Length == 0) continue;
                    if (!string.Equals(stem, g + "." + a + "-" + v,
                        StringComparison.OrdinalIgnoreCase)) continue;
                    identity = g + "." + a;
                    version = v;
                    break;
                }
            }
            // Filename fallback for valid archives with no matching Maven metadata.
            // Keep the complete prefix as identity: do not guess groupId/artifactId
            // separation. Prefer numeric dotted versions over single-digit versions.
            if (identity == null)
            {
                int separator = FindVersionBoundary(stem);
                if (separator < 1) return null;
                identity = stem.Substring(0, separator);
                version = stem.Substring(separator + 1);
            }
            return new Entry { Identity = identity, Version = version,
                Owner = owner, Scope = scope, Name = name };
        }

        private static int FindVersionBoundary(string stem)
        {
            int candidate = -1;
            for (int i = 1; i + 1 < stem.Length; i++)
            {
                if (stem[i] != '-' || !char.IsDigit(stem[i + 1])) continue;
                string tail = stem.Substring(i + 1);
                if (!tail.All(c => char.IsLetterOrDigit(c) || c == '.' ||
                    c == '_' || c == '-' || c == '+')) continue;
                if (tail.IndexOf('.') >= 0) return i;
                if (candidate < 0) candidate = i;
            }
            return candidate;
        }

        private static Report Summarize(List<Entry> entries, int unknown)
        {
            var conflicts = entries.GroupBy(x => x.Identity, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { Key = g.Key,
                    Versions = g.Select(x => x.Version).Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
                    Items = g.ToList() })
                .Where(g => g.Versions.Count > 1)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
            var message = new StringBuilder();
            message.AppendLine(conflicts.Count == 0 ?
                "Nessun conflitto di versione rilevato nei JAR Plain gestiti." :
                "CONFLITTI DI VERSIONE: " + conflicts.Count);
            foreach (var group in conflicts.Take(12))
            {
                message.AppendLine();
                message.AppendLine(group.Key);
                foreach (string version in group.Versions)
                {
                    string owners = string.Join(", ", group.Items
                        .Where(x => string.Equals(x.Version, version,
                            StringComparison.OrdinalIgnoreCase))
                        .Select(x => x.Owner + " [" + x.Scope + "]")
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(10));
                    message.AppendLine("  " + version + " <- " + owners);
                }
            }
            if (conflicts.Count > 12)
                message.AppendLine("... altri " + (conflicts.Count - 12) + " conflitti.");
            if (unknown != 0)
                message.AppendLine("\nAVVISO: " + unknown + " riferimenti non verificabili " +
                    "(file assente, path non gestito o coordinate non riconosciute).");
            message.AppendLine("\nAnalisi limitata ai JAR Plain gestiti. I JAR manuali e " +
                "l'effettiva compatibilità binaria delle versioni non sono verificati.");
            return new Report { ConflictCount = conflicts.Count,
                UnverifiedCount = unknown, Details = message.ToString().TrimEnd(),
                Libraries = entries.Select(ToLibrary).ToList(),
                Conflicts = conflicts.Select(group => new Conflict
                {
                    Identity = group.Key,
                    Versions = group.Versions.ToList(),
                    Libraries = group.Items.Select(ToLibrary).ToList()
                }).ToList()
            };
        }

        private static Library ToLibrary(Entry value)
        {
            return new Library { Identity = value.Identity, Version = value.Version,
                Owner = value.Owner, Scope = value.Scope, Name = value.Name };
        }
    }
}
