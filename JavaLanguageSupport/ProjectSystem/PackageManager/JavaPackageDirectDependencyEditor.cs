using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace JavaLanguageSupport.ProjectSystem.PackageManager
{
    // JPM-S1-SCOPES-20261123

    internal enum JavaDependencyOperation { Install, Update, Remove, ChangeScope }

    // F4F2111-DIRECT-DEPENDENCIES: preview first; commit only after user confirmation.
    // No build tool is executed, no external repository/credential file is edited.
    internal sealed class JavaDependencyChange
    {
        private readonly byte[] original;
        private readonly byte[] updated;
        internal string FilePath { get; private set; }
        internal string Summary { get; private set; }
        internal string BackupPath { get; private set; }

        internal JavaDependencyChange(string path, byte[] before, byte[] after, string summary)
        {
            FilePath = path;
            original = before;
            updated = after;
            Summary = summary;
        }

        internal void Apply()
        {
            if (!File.ReadAllBytes(FilePath).SequenceEqual(original))
                throw new IOException("Il file è cambiato dopo l'anteprima. Nessuna modifica eseguita.");
            if (original.SequenceEqual(updated))
                throw new InvalidOperationException("La dipendenza ha già lo stato richiesto.");
            string directory = Path.GetDirectoryName(FilePath);
            string name = Path.GetFileName(FilePath);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            BackupPath = Path.Combine(directory, name + ".java-backup-" + stamp);
            if (File.Exists(BackupPath))
                BackupPath += "-" + Guid.NewGuid().ToString("N");
            // Keep a recoverable copy, even if a file change unexpectedly fails.
            File.Copy(FilePath, BackupPath, false);
            string temp = Path.Combine(directory, "." + name + ".java-edit-" +
                Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None))
                {
                    stream.Write(updated, 0, updated.Length);
                    stream.Flush(true);
                }
                if (!File.ReadAllBytes(FilePath).SequenceEqual(original))
                    throw new IOException("Il file è stato modificato durante il salvataggio.");
                File.Replace(temp, FilePath, null);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
    }

    internal static class JavaPackageDirectDependencyEditor
    {
        private static readonly Regex CoordinatePart = new Regex(@"^[A-Za-z0-9_.-]+$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex VersionPart = new Regex(@"^[A-Za-z0-9_.+\-]+$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex GradleBlock = new Regex(
            @"(?m)^[ \t]*dependencies[ \t]*\{[ \t]*(?://[^\r\n]*)?\r?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex GradleClose = new Regex(
            @"(?m)^[ \t]*\}[ \t]*(?://[^\r\n]*)?\r?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex GradleDependency = new Regex(
            @"(?m)^[ \t]*(?<config>api|implementation|compileOnly|runtimeOnly|testImplementation|testCompileOnly|testRuntimeOnly|annotationProcessor|kapt|compile)[ \t]*(?:\([ \t]*)?(?<quote>['""])(?<group>[A-Za-z0-9_.-]+):(?<artifact>[A-Za-z0-9_.-]+):(?<version>[A-Za-z0-9_.+\-]+)\k<quote>[ \t]*\)?[ \t]*(?://[^\r\n]*)?\r?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        internal static JavaDependencyChange Plan(string projectFile, JavaDependencyOperation operation,
            string group, string artifact, string version, string scope = "compile")
        {
            scope = JavaPackageScopes.Require(scope);
            if (string.IsNullOrWhiteSpace(projectFile) || !File.Exists(projectFile))
                throw new IOException("Selezionare un progetto Java caricato.");
            if (!CoordinatePart.IsMatch(group ?? "") || !CoordinatePart.IsMatch(artifact ?? ""))
                throw new ArgumentException("Le coordinate Maven contengono caratteri non ammessi.");
            if ((operation == JavaDependencyOperation.Install ||
                operation == JavaDependencyOperation.Update) && !VersionPart.IsMatch(version ?? ""))
                throw new ArgumentException("Selezionare una versione letterale valida.");
            var snapshot = JavaPackageManagerInstalledReader.Read(projectFile);
            string folder = Path.GetDirectoryName(Path.GetFullPath(projectFile));
            string path;
            if (string.Equals(snapshot.Backend, "Maven", StringComparison.OrdinalIgnoreCase))
                path = Path.Combine(folder, "pom.xml");
            else if (string.Equals(snapshot.Backend, "Gradle", StringComparison.OrdinalIgnoreCase))
            {
                string groovy = Path.Combine(folder, "build.gradle");
                string kotlin = Path.Combine(folder, "build.gradle.kts");
                if (File.Exists(groovy) && File.Exists(kotlin))
                    throw new NotSupportedException("Sono presenti entrambi i build script Gradle.");
                path = File.Exists(groovy) ? groovy : kotlin;
            }
            else throw new NotSupportedException(
                "I JAR Plain saranno gestiti nel pack dedicato; nessuna modifica eseguita.");
            if (!File.Exists(path)) throw new IOException("File delle dipendenze non trovato: " + path);
            byte[] raw = File.ReadAllBytes(path);
            bool bom = raw.Length >= 3 && raw[0] == 239 && raw[1] == 187 && raw[2] == 191;
            // Never corrupt UTF-16 or other encodings; fail instead of guessing.
            string source = new UTF8Encoding(false, true).GetString(raw, bom ? 3 : 0,
                raw.Length - (bom ? 3 : 0));
            string nl = source.Contains("\r\n") ? "\r\n" : "\n";
            if (source.Contains("\r") && !source.Contains("\r\n"))
                throw new NotSupportedException("Formato righe CR non supportato.");
            if (source.Contains("\r\n") && source.Replace("\r\n", "").Contains("\n"))
                throw new NotSupportedException("Terminatori di riga misti.");
            string result = string.Equals(snapshot.Backend, "Maven", StringComparison.OrdinalIgnoreCase)
                ? EditMaven(source, nl, operation, group, artifact, version, scope)
                : EditGradle(source, nl, path.EndsWith(".kts", StringComparison.OrdinalIgnoreCase),
                    operation, group, artifact, version, scope);
            byte[] content = new UTF8Encoding(false).GetBytes(result);
            byte[] target = new byte[content.Length + (bom ? 3 : 0)];
            if (bom) { target[0] = 239; target[1] = 187; target[2] = 191; }
            Buffer.BlockCopy(content, 0, target, bom ? 3 : 0, content.Length);
            string action = operation == JavaDependencyOperation.Install ? "Installazione" :
                operation == JavaDependencyOperation.Update ? "Aggiornamento" :
                operation == JavaDependencyOperation.ChangeScope ? "Cambio scope" : "Rimozione";
            string summary = action + " di " + group + ":" + artifact +
                (operation == JavaDependencyOperation.Remove ? "" :
                    operation == JavaDependencyOperation.ChangeScope ? " [" + scope + "]" :
                    operation == JavaDependencyOperation.Install ? " → " + version + " [" + scope + "]" :
                    " → " + version);
            return new JavaDependencyChange(path, raw, target, summary);
        }

        private static XmlElement Direct(XmlNode parent, string name)
        {
            var matches = parent.ChildNodes.OfType<XmlElement>()
                .Where(x => x.LocalName == name).ToList();
            if (matches.Count > 1)
                throw new NotSupportedException("Più elementi XML '" + name + "': modifica non sicura.");
            return matches.FirstOrDefault();
        }

        private static string EditMaven(string source, string nl, JavaDependencyOperation op,
            string group, string artifact, string version, string scope)
        {
            var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
            var settings = new XmlReaderSettings { XmlResolver = null,
                DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 8 * 1024 * 1024 };
            using (var reader = XmlReader.Create(new StringReader(source), settings)) document.Load(reader);
            var root = document.DocumentElement;
            if (root == null || root.LocalName != "project")
                throw new NotSupportedException("Il file non è un POM Maven valido.");
            var dependencies = Direct(root, "dependencies");
            if (dependencies == null)
            {
                if (op != JavaDependencyOperation.Install)
                    throw new InvalidOperationException("Dipendenza non dichiarata nel POM.");
                dependencies = document.CreateElement(root.Prefix, "dependencies", root.NamespaceURI);
                var closing = root.LastChild;
                if (closing is XmlWhitespace || closing is XmlSignificantWhitespace)
                {
                    root.InsertBefore(document.CreateWhitespace(nl + "  "), closing);
                    root.InsertBefore(dependencies, closing);
                }
                else
                {
                    root.AppendChild(document.CreateWhitespace(nl + "  "));
                    root.AppendChild(dependencies);
                    root.AppendChild(document.CreateWhitespace(nl));
                }
            }
            var matches = dependencies.ChildNodes.OfType<XmlElement>()
                .Where(x => x.LocalName == "dependency" &&
                    string.Equals(Direct(x, "groupId") == null ? null : Direct(x, "groupId").InnerText.Trim(), group,
                        StringComparison.Ordinal) &&
                    string.Equals(Direct(x, "artifactId") == null ? null : Direct(x, "artifactId").InnerText.Trim(), artifact,
                        StringComparison.Ordinal)).ToList();
            if (matches.Count > 1)
                throw new NotSupportedException("Dipendenza dichiarata più volte nel POM.");
            var existing = matches.FirstOrDefault();
            if (op == JavaDependencyOperation.Install)
            {
                if (existing != null) throw new InvalidOperationException(
                    "Dipendenza già presente: utilizzare Aggiorna.");
                var item = document.CreateElement(root.Prefix, "dependency", root.NamespaceURI);
                AddMavenValue(document, item, "groupId", group, nl);
                AddMavenValue(document, item, "artifactId", artifact, nl);
                AddMavenValue(document, item, "version", version, nl);
                if (scope != "compile") AddMavenValue(document, item, "scope", scope, nl);
                item.AppendChild(document.CreateWhitespace(nl + "    "));
                var tail = dependencies.LastChild;
                if (tail is XmlWhitespace || tail is XmlSignificantWhitespace)
                {
                    dependencies.InsertBefore(document.CreateWhitespace(nl + "    "), tail);
                    dependencies.InsertBefore(item, tail);
                }
                else
                {
                    dependencies.AppendChild(document.CreateWhitespace(nl + "    "));
                    dependencies.AppendChild(item);
                    dependencies.AppendChild(document.CreateWhitespace(nl + "  "));
                }
            }
            else
            {
                if (existing == null) throw new InvalidOperationException(
                    "Dipendenza diretta non presente nel POM.");
                if (op == JavaDependencyOperation.Remove)
                {
                    var previous = existing.PreviousSibling;
                    dependencies.RemoveChild(existing);
                    if (previous is XmlWhitespace || previous is XmlSignificantWhitespace)
                        dependencies.RemoveChild(previous);
                }
                else if (op == JavaDependencyOperation.ChangeScope)
                {
                    var scopeNode = Direct(existing, "scope");
                    if (scopeNode != null && !JavaPackageScopes.IsSupported(scopeNode.InnerText))
                        throw new NotSupportedException("Scope POM esistente non supportato.");
                    if (scope == "compile")
                    {
                        if (scopeNode != null)
                        {
                            var previous = scopeNode.PreviousSibling;
                            existing.RemoveChild(scopeNode);
                            if (previous is XmlWhitespace || previous is XmlSignificantWhitespace)
                                existing.RemoveChild(previous);
                        }
                    }
                    else if (scopeNode != null) scopeNode.InnerText = scope;
                    else AddMavenValue(document, existing, "scope", scope, nl);
                }
                else
                {
                    var versionNode = Direct(existing, "version");
                    if (versionNode == null || versionNode.InnerText.Contains("${"))
                        throw new NotSupportedException(
                            "Versione gestita da proprietà/BOM/parent: aggiornare manualmente il POM.");
                    versionNode.InnerText = version;
                }
            }
            // XmlReader normalizes CRLF to LF per XML rules; restore project line endings.
            string xml = document.OuterXml;
            return nl == "\r\n" ? xml.Replace("\r\n", "\n").Replace("\n", "\r\n") : xml;
        }

        private static void AddMavenValue(XmlDocument document, XmlElement parent,
            string name, string value, string nl)
        {
            var node = document.CreateElement(parent.Prefix, name, parent.NamespaceURI);
            node.InnerText = value;
            parent.AppendChild(document.CreateWhitespace(nl + "      "));
            parent.AppendChild(node);
        }

        private static string EditGradle(string source, string nl, bool kotlin,
            JavaDependencyOperation op, string group, string artifact, string version, string scope)
        {
            var opens = GradleBlock.Matches(source).Cast<Match>().ToList();
            if (opens.Count != 1)
                throw new NotSupportedException("Atteso un solo blocco dependencies { } in build.gradle.");
            var open = opens[0];
            var close = GradleClose.Matches(source).Cast<Match>()
                .FirstOrDefault(m => m.Index > open.Index + open.Length);
            if (close == null) throw new NotSupportedException("Chiusura dependencies { } non trovata.");
            string body = source.Substring(open.Index + open.Length,
                close.Index - open.Index - open.Length);
            if (body.IndexOf('{') >= 0 || body.IndexOf('}') >= 0)
                throw new NotSupportedException("Blocco Gradle annidato: modifica automatica non sicura.");
            var matches = GradleDependency.Matches(body).Cast<Match>()
                .Where(x => string.Equals(x.Groups["group"].Value, group, StringComparison.Ordinal) &&
                    string.Equals(x.Groups["artifact"].Value, artifact, StringComparison.Ordinal)).ToList();
            if (matches.Count > 1)
                throw new NotSupportedException("Dipendenza Gradle dichiarata più volte.");
            var existing = matches.FirstOrDefault();
            string coordinate = group + ":" + artifact;
            if (existing == null && body.Contains(coordinate))
                throw new NotSupportedException("Coordinata presente in una dichiarazione non letterale.");
            if (op == JavaDependencyOperation.Install)
            {
                if (existing != null) throw new InvalidOperationException("Dipendenza già presente nel progetto.");
                string indent = "    ";
                var first = GradleDependency.Match(body);
                if (first.Success) indent = first.Value.Substring(0, first.Value.Length - first.Value.TrimStart(' ', '\t').Length);
                string declaration = kotlin ?
                    JavaPackageScopes.ToGradle(scope) + "(\"" + coordinate + ":" + version + "\")" :
                    JavaPackageScopes.ToGradle(scope) + " '" + coordinate + ":" + version + "'";
                string insertion = (body.EndsWith(nl, StringComparison.Ordinal) ? "" : nl) +
                    indent + declaration + nl;
                return source.Insert(close.Index, insertion);
            }
            if (existing == null)
                throw new InvalidOperationException("Dipendenza diretta non presente in build.gradle.");
            int start = open.Index + open.Length + existing.Index;
            if (op == JavaDependencyOperation.ChangeScope)
            {
                var config = existing.Groups["config"];
                string replacement = JavaPackageScopes.ToGradle(scope);
                return source.Remove(start + config.Index - existing.Index, config.Length)
                    .Insert(start + config.Index - existing.Index, replacement);
            }
            if (op == JavaDependencyOperation.Update)
            {
                var oldVersion = existing.Groups["version"];
                return source.Remove(start + oldVersion.Index - existing.Index, oldVersion.Length)
                    .Insert(start + oldVersion.Index - existing.Index, version);
            }
            int removeEnd = start + existing.Length;
            if (source.Substring(removeEnd).StartsWith(nl, StringComparison.Ordinal)) removeEnd += nl.Length;
            return source.Remove(start, removeEnd - start);
        }
    }
}
