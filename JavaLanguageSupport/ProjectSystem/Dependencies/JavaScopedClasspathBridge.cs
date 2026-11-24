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
        private const string ScopeElement = "JavaPackageManagerScope";
        internal sealed class Result
        {
            internal bool Active;
            internal bool Changed;
            internal bool Created;
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
            var result = new Result();
            try
            {
                // Keep existing single-Plain-root safety: a global unmanaged JDT
                // setting may otherwise leak across unrelated projects.
                var loaded = JavaCpsWorkspaceContext.LoadedProjects();
                if (loaded.Length != 1 || !string.Equals(loaded[0], projectFile,
                    StringComparison.OrdinalIgnoreCase))
                {
                    result.Note = "Scoped classpath: progetto Plain non isolato nella soluzione.";
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
                bool ourProject = hasProject && IsManaged(eclipseProject);
                bool ourClasspath = hasClasspath && IsManaged(eclipseClasspath);

                var settings = new XmlReaderSettings { XmlResolver = null,
                    DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 8 * 1024 * 1024 };
                XDocument doc;
                using (var reader = XmlReader.Create(projectFile, settings))
                    doc = XDocument.Load(reader);
                if (doc.Root == null || doc.Root.Name.LocalName != "Project") return result;
                if (!string.Equals(DescendantText(doc.Root, "JavaBuildSystem") ?? "Plain",
                    "Plain", StringComparison.OrdinalIgnoreCase)) return result;

                var parsed = new Dictionary<string, JarEntry>(StringComparer.OrdinalIgnoreCase);
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
                }
                // Import-provided explicit JARs absent from the XML are treated as
                // compile, exactly as the original CPS classpath did.
                foreach (string path in cpsReferences ?? new string[0])
                {
                    if (!string.IsNullOrWhiteSpace(path) && path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                    {
                        string full = Path.GetFullPath(path);
                        if (!parsed.ContainsKey(full)) AddJar(parsed, full, false);
                    }
                }
                if (!hasScopedPackage && !ourProject && !ourClasspath) return result;
                if ((hasProject && !ourProject) || (hasClasspath && !ourClasspath))
                {
                    result.Note = "Scoped classpath: .project/.classpath esistenti non gestiti, nessun file sovrascritto.";
                    return result;
                }
                if ((ourProject && !hasClasspath) || (ourClasspath && !hasProject))
                {
                    result.Note = "Scoped classpath: coppia di file Eclipse incompleta; intervenire manualmente.";
                    return result;
                }

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
                if (sources.Count == 0)
                {
                    result.Note = "Scoped classpath: nessuna source root valida; modello Eclipse non generato.";
                    return result;
                }
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
                var classpath = new XDocument(new XDeclaration("1.0", "UTF-8", null),
                    new XElement("classpath", new XComment(Marker), sources,
                        new XElement("classpathentry", new XAttribute("kind", "con"),
                            new XAttribute("path", "org.eclipse.jdt.launching.JRE_CONTAINER")),
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
                bool changed = SaveOwned(eclipseClasspath, Encode(classpath), root);
                // Write .classpath before .project: JDT LS should never import a
                // half-initialized Eclipse project during first-time migration.
                bool createdProject = !hasProject;
                changed |= SaveOwned(eclipseProject, Encode(descriptor), root);
                result.Active = true;
                result.Changed = changed;
                result.Created = createdProject;
                result.Note = changed ? "Scoped Eclipse classpath aggiornato." : "Scoped Eclipse classpath invariato.";
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

        private static bool SaveOwned(string file, byte[] bytes, string root)
        {
            if (File.Exists(file)) RejectRedirect(file);
            if (File.Exists(file) && File.ReadAllBytes(file).SequenceEqual(bytes)) return false;
            if (File.Exists(file) && !IsManaged(file))
                throw new IOException("Rifiutata sovrascrittura di file Eclipse non gestito: " + file);
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
