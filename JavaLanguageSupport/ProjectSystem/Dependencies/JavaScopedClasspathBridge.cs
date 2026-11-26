using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using JavaLanguageSupport.LanguageServer;

namespace JavaLanguageSupport.ProjectSystem.Dependencies
{
    /// <summary>
    /// JPM-S1.2: bridge from explicit Plain JavaJarReference scopes to the
    /// Eclipse/JDT model. JDT LS's unmanaged-project referencedLibraries is
    /// global and has no per-source-root scope; an Eclipse .classpath is needed.
    /// Never touches Maven/Gradle or existing user-owned Eclipse metadata.
    /// </summary>
    internal static class JavaScopedClasspathBridge
    {
        private const string Marker = "JavaLanguageSupportVS:JPM-S1.2:Managed";
        // JPM-S122-CLASSPATH-LIVE-20261124: serialize SaveOwned operations.
        // CPS, JPM and the asynchronous JDT LS pump may sync concurrently.
        private static readonly object SyncGate = new object();
        // JPM-S125-MANAGED-CLASSPATH-RECONCILE-20261124:
        // Remember all literal direct JavaJarReference paths seen during this
        // VS session. Once a reference is removed from .javaproj, a late CPS
        // snapshot must not re-add it to .classpath as an imported JAR.
        // The existing managed lib/java-packages exclusion remains in force.
        private static readonly Dictionary<string, HashSet<string>> HistoricExplicitJars =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private const string ScopeElement = "JavaPackageManagerScope";
        internal sealed class Result
        {
            internal bool Active;
            internal bool Changed;
            internal bool Created;
            internal bool CreatedClasspath;
            internal bool CreatedProject;
            internal string Note;
        }

        private sealed class JarEntry
        {
            internal string Path;
            internal bool TestOnly;
        }

        internal static Result Synchronize(string projectFile, string[] cpsReferences,
            string[] sourceRoots)
        {
            lock (SyncGate)
                return SynchronizeCore(projectFile, cpsReferences, sourceRoots);
        }

        private static Result SynchronizeCore(string projectFile, string[] cpsReferences,
            string[] sourceRoots)
        {
            var result = new Result();
            try
            {
                // JPM-S124: project-file authority. The caller supplies the exact
                // .javaproj being opened/updated; this method writes only its own
                // .project/.classpath. CPS registration can be asynchronous and
                // must not suppress the initial creation or post-JPM refresh.
                // The XML backend check below still excludes Maven and Gradle.
                if (!string.Equals(Path.GetExtension(projectFile), ".javaproj",
                    StringComparison.OrdinalIgnoreCase))
                {
                    result.Note = "Scoped classpath: file progetto non .javaproj.";
                    return result;
                }
                string root = Path.GetDirectoryName(Path.GetFullPath(projectFile));
                string eclipseProject = Path.Combine(root, ".project");
                string eclipseClasspath = Path.Combine(root, ".classpath");
                bool hasProject = File.Exists(eclipseProject);
                bool hasClasspath = File.Exists(eclipseClasspath);
                if (hasProject) RejectRedirect(eclipseProject);
                if (hasClasspath) RejectRedirect(eclipseClasspath);
                string cache = Path.Combine(root, ".java-vs");
                if (Directory.Exists(cache)) RejectRedirect(cache);
                // JPM-S126-OWNERSHIP-RECOVERY-20261124
                // JDT LS may rewrite Eclipse metadata, removing XML comments.
                // The old comment-only IsManaged then permanently blocked JPM
                // reconciliation with "file esistenti non gestiti". Recover only
                // descriptors with the exact VSIX generated project identity and
                // recognizable VSIX classpath outputs. Never claim a user .project.
                bool markedProject = hasProject && IsManaged(eclipseProject);
                bool markedClasspath = hasClasspath && IsManaged(eclipseClasspath);
                bool ourProject = markedProject ||
                    (hasProject && IsGeneratedDescriptor(eclipseProject, projectFile));
                bool ourClasspath = markedClasspath ||
                    (hasClasspath && ourProject && IsGeneratedClasspath(eclipseClasspath));
                bool recovered = (ourProject && !markedProject) ||
                    (ourClasspath && !markedClasspath);

                var settings = new XmlReaderSettings { XmlResolver = null,
                    DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 8 * 1024 * 1024 };
                XDocument doc;
                using (var reader = XmlReader.Create(projectFile, settings))
                    doc = XDocument.Load(reader);
                if (doc.Root == null || doc.Root.Name.LocalName != "Project") return result;
                if (!string.Equals(DescendantText(doc.Root, "JavaBuildSystem") ?? "Plain",
                    "Plain", StringComparison.OrdinalIgnoreCase)) return result;

                var parsed = new Dictionary<string, JarEntry>(StringComparer.OrdinalIgnoreCase);
                var explicitNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool hasScopedPackage = false;
                foreach (var group in doc.Root.Elements().Where(e => e.Name.LocalName == "ItemGroup"))
                foreach (var item in group.Elements().Where(e => e.Name.LocalName == "JavaJarReference"))
                {
                    string include = (string)item.Attribute("Include");
                    if (string.IsNullOrWhiteSpace(include) ||
                        include.IndexOfAny(new[] { '*', '?', '$', '%', ';', '\r', '\n' }) >= 0 ||
                        !include.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;
                    string scope = item.Elements().Where(e => e.Name.LocalName == ScopeElement)
                        .Select(e => e.Value.Trim()).FirstOrDefault() ?? "compile";
                    scope = scope.ToLowerInvariant();
                    if (scope != "compile" && scope != "provided" && scope != "runtime" && scope != "test")
                        throw new InvalidDataException("Scope Plain non riconosciuto: " + scope);
                    if (scope != "compile") hasScopedPackage = true;
                    string path = Path.GetFullPath(Path.IsPathRooted(include) ? include : Path.Combine(root, include));
                    AddJar(parsed, path, scope == "runtime" || scope == "test");
                    explicitNow.Add(path);
                }
                // Evaluated CPS references may be ahead of or behind the saved
                // .javaproj during a package transaction. Never resurrect a removed
                // JPM-managed JAR from a stale CPS snapshot: the *disk XML* is
                // authoritative for lib/java-packages. Preserve evaluated JARs
                // declared in imported MSBuild files or wildcard items elsewhere.
                string managedRoot = Path.Combine(root, "lib", "java-packages")
                    .TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                HashSet<string> previouslyExplicit;
                // SynchronizeCore is serialized by SyncGate. Keep the history
                // across successive CPS snapshots, including stale ones.
                if (!HistoricExplicitJars.TryGetValue(projectFile, out previouslyExplicit))
                    HistoricExplicitJars[projectFile] = previouslyExplicit =
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                previouslyExplicit.UnionWith(explicitNow);
                foreach (string path in cpsReferences ?? new string[0])
                {
                    if (string.IsNullOrWhiteSpace(path) ||
                        !path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;
                    string full = Path.GetFullPath(path);
                    if (full.StartsWith(managedRoot, StringComparison.OrdinalIgnoreCase))
                        continue;
                    // A previously explicit direct reference removed from the
                    // saved .javaproj is NOT an imported MSBuild reference.
                    // Avoid restoring a removed manual JAR from stale CPS.
                    if (previouslyExplicit.Contains(full) && !explicitNow.Contains(full))
                        continue;
                    if (!parsed.ContainsKey(full)) AddJar(parsed, full, false);
                }
                // JPM-S124: build a managed Eclipse model for every Plain
                // project with valid source roots, even with zero scoped JARs.
                // This covers manual JavaJarReference and restoring BOTH files
                // after deletion when all packages happen to be compile-only.
                // Never overwrite an existing user-owned Eclipse descriptor.
                if ((hasProject && !ourProject) || (hasClasspath && !ourClasspath))
                {
                    result.Note = "Scoped classpath: .project/.classpath esistenti non gestiti, nessun file sovrascritto.";
                    return result;
                }
                // JPM-S124: restore missing owned half. SaveOwned only writes
                // missing files or previously managed files; user-owned files
                // are rejected above and are NEVER overwritten.
                // JPM-S124: restore missing owned half.

                var sources = new List<XElement>();
                string prefix = root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                foreach (string relative in (sourceRoots ?? new string[0]).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
                        relative.IndexOfAny(new[] { '*', '?', '$', '%', '\r', '\n' }) >= 0) continue;
                    string full = Path.GetFullPath(Path.Combine(root, relative));
                    if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                        !Directory.Exists(full)) continue;
                    string normalized = full.Substring(prefix.Length).Replace('\\', '/');
                    bool test = normalized.Equals("src/test/java", StringComparison.OrdinalIgnoreCase) ||
                        normalized.StartsWith("src/test/java/", StringComparison.OrdinalIgnoreCase) ||
                        normalized.Equals("src/test", StringComparison.OrdinalIgnoreCase) ||
                        normalized.StartsWith("src/test/", StringComparison.OrdinalIgnoreCase);
                    XElement source = new XElement("classpathentry", new XAttribute("kind", "src"),
                        new XAttribute("path", normalized));
                    // JDT requires test sources to have a distinct output folder.
                    // Keep these outputs separate from the Plain MSBuild compiler.
                    source.SetAttributeValue("output", test
                        ? ".java-vs/jdtls-test-classes"
                        : ".java-vs/jdtls-classes");
                    if (test) source.Add(new XElement("attributes", new XElement("attribute",
                        new XAttribute("name", "test"), new XAttribute("value", "true"))));
                    sources.Add(source);
                }
                // JPM-S2D421-JDT-JARONLY-20261126
                // A valid Plain project can be JAR-only (zero existing source roots).
                // Keep its actual JARs, JRE container and output in the Eclipse
                // model instead of preventing .project/.classpath creation.
                // No synthetic source directory is created.
                // Eclipse test-only library attribute makes the library visible to
                // test source roots only. Plain runtime is test-compile-visible.
                var entries = parsed.Values.OrderBy(e => e.TestOnly ? 1 : 0)
                    .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase).Select(entry =>
                {
                    var element = new XElement("classpathentry", new XAttribute("kind", "lib"),
                        new XAttribute("path", entry.Path.Replace('\\', '/')));
                    if (entry.TestOnly) element.Add(new XElement("attributes", new XElement("attribute",
                        new XAttribute("name", "test"), new XAttribute("value", "true"))));
                    return element;
                }).ToArray();
                // Keep JDT LS's selected JDK container when regenerating our
                // own .classpath. A generic path is used on first creation.
                string jreContainer = ourClasspath && hasClasspath
                    ? ReadCurrentJreContainer(eclipseClasspath) : null;
                if (string.IsNullOrEmpty(jreContainer))
                    jreContainer = "org.eclipse.jdt.launching.JRE_CONTAINER";
                var classpath = new XDocument(new XDeclaration("1.0", "UTF-8", null),
                    new XElement("classpath", new XComment(Marker), sources,
                        new XElement("classpathentry", new XAttribute("kind", "con"),
                            new XAttribute("path", jreContainer)),
                        entries,
                        new XElement("classpathentry", new XAttribute("kind", "output"),
                            new XAttribute("path", ".java-vs/jdtls-classes"))));
                string projectName = Path.GetFileNameWithoutExtension(projectFile) + "-vsjava-" + ShortHash(projectFile);
                var descriptor = new XDocument(new XDeclaration("1.0", "UTF-8", null),
                    new XElement("projectDescription", new XComment(Marker),
                        new XElement("name", projectName),
                        new XElement("comment", "Generated by JavaLanguageSupportVS for JDT LS scopes"),
                        new XElement("projects"),
                        new XElement("buildSpec", new XElement("buildCommand",
                            new XElement("name", "org.eclipse.jdt.core.javabuilder"),
                            new XElement("arguments"))),
                        new XElement("natures", new XElement("nature", "org.eclipse.jdt.core.javanature"))));
                bool changed = SaveOwned(eclipseClasspath, Encode(classpath), root, ourClasspath);
                // Write .classpath before .project: JDT LS should never import a
                // half-initialized Eclipse project during first-time migration.
                bool createdProject = !hasProject;
                changed |= SaveOwned(eclipseProject, Encode(descriptor), root, ourProject);
                result.Active = true;
                result.Changed = changed;
                result.Created = createdProject;
                result.CreatedClasspath = !hasClasspath;
                result.CreatedProject = !hasProject;
                result.Note = recovered
                    ? (changed ? "Scoped Eclipse classpath recuperato: metadati JDT riconosciuti e aggiornati."
                        : "Scoped Eclipse classpath riconosciuto: JDT ha rimosso il marker, dati gia' allineati.")
                    : (changed ? "Scoped Eclipse classpath aggiornato."
                        : "Scoped Eclipse classpath invariato.");
                if (sources.Count == 0)
                    result.Note += " Progetto JAR-only: nessuna source root esistente.";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                ex is XmlException || ex is ArgumentException || ex is NotSupportedException)
            {
                result.Note = "Scoped classpath non applicato: " + ex.GetType().Name + ": " + ex.Message;
            }
            return result;
        }

        private static string DescendantText(XElement node, string name)
        {
            return node.Descendants().Where(x => x.Name.LocalName == name)
                .Select(x => x.Value.Trim()).FirstOrDefault();
        }

        private static void AddJar(Dictionary<string, JarEntry> map, string path, bool testOnly)
        {
            JarEntry current;
            if (map.TryGetValue(path, out current)) current.TestOnly &= testOnly;
            else map.Add(path, new JarEntry { Path = path, TestOnly = testOnly });
        }

        // JPM-S126: Eclipse can drop our XML comment when persisting .project
        // or .classpath. An exact VSIX-specific descriptor signature is stronger
        // than the comment and allows safe regeneration after that rewrite.
        private static bool IsGeneratedDescriptor(string file, string projectFile)
        {
            try
            {
                var settings = new XmlReaderSettings { XmlResolver = null,
                    DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 8 * 1024 * 1024 };
                XDocument doc;
                using (var reader = XmlReader.Create(file, settings)) doc = XDocument.Load(reader);
                var root = doc.Root;
                if (root == null || root.Name.LocalName != "projectDescription") return false;
                string expected = Path.GetFileNameWithoutExtension(projectFile) +
                    "-vsjava-" + ShortHash(projectFile);
                if (!string.Equals((string)root.Element("name"), expected, StringComparison.Ordinal))
                    return false;
                bool nature = root.Descendants("natures").Elements("nature")
                    .Any(n => n.Value == "org.eclipse.jdt.core.javanature");
                bool builder = root.Descendants("buildSpec").Descendants("name")
                    .Any(n => n.Value == "org.eclipse.jdt.core.javabuilder");
                // Do not adopt user Eclipse projects with additional builders or natures.
                return nature && builder &&
                    root.Descendants("natures").Elements("nature").Count() == 1 &&
                    root.Descendants("buildSpec").Elements("buildCommand").Count() == 1;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                ex is XmlException || ex is ArgumentException || ex is NotSupportedException)
            { return false; }
        }

        // Only the JDT Java runtime root or its concrete JDK specialization
        // are valid. Do not treat arbitrary Eclipse containers as VSIX-owned.
        private static bool IsVsixJreContainer(string path)
        {
            const string root = "org.eclipse.jdt.launching.JRE_CONTAINER";
            return string.Equals(path, root, StringComparison.Ordinal) ||
                (path != null && path.StartsWith(root + "/", StringComparison.Ordinal) &&
                 path.Length > root.Length + 1);
        }

        private static string ReadCurrentJreContainer(string file)
        {
            try
            {
                var settings = new XmlReaderSettings { XmlResolver = null,
                    DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 8 * 1024 * 1024 };
                XDocument doc;
                using (var reader = XmlReader.Create(file, settings)) doc = XDocument.Load(reader);
                if (doc.Root == null || doc.Root.Name.LocalName != "classpath") return null;
                var containers = doc.Root.Elements().Where(e => e.Name.LocalName == "classpathentry" &&
                    (string)e.Attribute("kind") == "con").ToList();
                if (containers.Count != 1) return null;
                string path = (string)containers[0].Attribute("path");
                return IsVsixJreContainer(path) ? path : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                ex is XmlException || ex is ArgumentException || ex is NotSupportedException)
            { return null; }
        }

        private static bool IsGeneratedClasspath(string file)
        {
            try
            {
                var settings = new XmlReaderSettings { XmlResolver = null,
                    DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 8 * 1024 * 1024 };
                XDocument doc;
                using (var reader = XmlReader.Create(file, settings)) doc = XDocument.Load(reader);
                var root = doc.Root;
                if (root == null || root.Name.LocalName != "classpath") return false;
                var entries = root.Elements().ToList();
                if (entries.Count == 0 || entries.Any(e => e.Name.LocalName != "classpathentry")) return false;
                var outputs = entries.Where(e => (string)e.Attribute("kind") == "output").ToList();
                if (outputs.Count != 1 ||
                    (string)outputs[0].Attribute("path") != ".java-vs/jdtls-classes") return false;
                var sources = entries.Where(e => (string)e.Attribute("kind") == "src").ToList();
                // A structurally verified VSIX classpath can have zero source entries.
                if (sources.Any(e => {
                    string output = (string)e.Attribute("output");
                    return output != ".java-vs/jdtls-classes" &&
                        output != ".java-vs/jdtls-test-classes";
                })) return false;
                var containers = entries.Where(e => (string)e.Attribute("kind") == "con").ToList();
                // JPM-S127-JRE-CONTAINER-20261124
                // JDT LS specializes a generic JRE_CONTAINER to a concrete VM.
                // That is still the VSIX-generated classpath: ownership must
                // not disappear merely because the selected JDK is explicit.
                if (containers.Count != 1 ||
                    !IsVsixJreContainer((string)containers[0].Attribute("path"))) return false;
                return entries.All(e => {
                    string kind = (string)e.Attribute("kind");
                    return kind == "src" || kind == "lib" || kind == "con" || kind == "output";
                });
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                ex is XmlException || ex is ArgumentException || ex is NotSupportedException)
            { return false; }
        }

        // JPM-S129-OWNED-ROOT-20261124: the existence of our generated
        // .project must NOT prevent announcing the CPS root to JDT LS.
        // The prior CanManagePlain() intentionally denies unmanaged settings
        // when .project exists; it must not gate workspace folder registration.
        // Do not adopt user-maintained Eclipse files.
        internal static bool CanBootstrapPlainWorkspace(string projectFile)
        {
            if (string.IsNullOrWhiteSpace(projectFile) || !File.Exists(projectFile)) return false;
            string root = Path.GetDirectoryName(Path.GetFullPath(projectFile));
            string descriptor = Path.Combine(root, ".project");
            string classpath = Path.Combine(root, ".classpath");
            return (!File.Exists(descriptor) || IsManaged(descriptor) ||
                    IsGeneratedDescriptor(descriptor, projectFile)) &&
                   (!File.Exists(classpath) || IsManaged(classpath) ||
                    IsGeneratedClasspath(classpath));
        }

        private static bool IsManaged(string file)
        {
            var settings = new XmlReaderSettings { XmlResolver = null,
                DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 8 * 1024 * 1024 };
            using (var reader = XmlReader.Create(file, settings))
            {
                var document = XDocument.Load(reader);
                return document.DescendantNodes().OfType<XComment>()
                    .Any(x => x.Value.Trim() == Marker);
            }
        }

        private static byte[] Encode(XDocument document)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = XmlWriter.Create(stream, new XmlWriterSettings
                {
                    Encoding = new UTF8Encoding(false), Indent = true,
                    IndentChars = "  ", NewLineChars = "\r\n", NewLineHandling = NewLineHandling.Replace
                })) document.Save(writer);
                return stream.ToArray();
            }
        }

        private static string ShortHash(string value)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(
                    Path.GetFullPath(value).ToUpperInvariant())), 0, 5).Replace("-", "").ToLowerInvariant();
        }

        private static void RejectRedirect(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Percorso Eclipse/JDT reindirizzato non supportato: " + path);
        }

        // verifiedOwned is true only after the explicit marker or the tight
        // .project/.classpath structural fingerprints above have been verified.
        private static bool SemanticallyEqual(string file, byte[] expected)
        {
            try
            {
                var settings = new XmlReaderSettings { XmlResolver = null,
                    DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 8 * 1024 * 1024 };
                XDocument actual;
                using (var reader = XmlReader.Create(file, settings)) actual = XDocument.Load(reader);
                var desired = XDocument.Parse(Encoding.UTF8.GetString(expected));
                actual.DescendantNodes().OfType<XComment>()
                    .Where(n => n.Value.Trim() == Marker).ToList().ForEach(n => n.Remove());
                desired.DescendantNodes().OfType<XComment>()
                    .Where(n => n.Value.Trim() == Marker).ToList().ForEach(n => n.Remove());
                return XNode.DeepEquals(actual.Root, desired.Root);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                ex is XmlException || ex is ArgumentException || ex is NotSupportedException)
            { return false; }
        }

        private static bool SaveOwned(string file, byte[] bytes, string root, bool verifiedOwned)
        {
            if (File.Exists(file)) RejectRedirect(file);
            if (File.Exists(file) && File.ReadAllBytes(file).SequenceEqual(bytes)) return false;
            if (File.Exists(file) && !verifiedOwned)
                throw new IOException("Rifiutata sovrascrittura di file Eclipse non gestito: " + file);
            // JDT LS frequently reserializes the XML and strips our ownership
            // comment. If elements/attributes already match, do NOT write just
            // to restore the comment: it would cause a watcher feedback loop.
            if (File.Exists(file) && SemanticallyEqual(file, bytes)) return false;
            string temp = Path.Combine(root, ".java-jdtls-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { output.Write(bytes, 0, bytes.Length); output.Flush(true); }
                if (File.Exists(file))
                {
                    string backupDir = Path.Combine(root, ".java-vs", "jdtls-meta-backups");
                    if (Directory.Exists(backupDir)) RejectRedirect(backupDir);
                    Directory.CreateDirectory(backupDir);
                    string backup = Path.Combine(backupDir, Path.GetFileName(file) + "-" +
                        DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    File.Replace(temp, file, backup);
                }
                else File.Move(temp, file);
                return true;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
