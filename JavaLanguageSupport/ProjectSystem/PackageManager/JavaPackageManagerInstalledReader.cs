using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace JavaLanguageSupport.ProjectSystem.PackageManager
{
    // JPM-S1-SCOPES-20261123

    /// <summary>Local, read-only DIRECT dependency snapshot. No build tool is executed.</summary>
    internal static class JavaPackageManagerInstalledReader
    {
        private static readonly Regex GradleLiteral = new Regex(
            "(?m)^\\s*(?<config>api|implementation|compileOnly|runtimeOnly|testImplementation|testCompileOnly|testRuntimeOnly|annotationProcessor|kapt|compile)\\s*(?:\\(\\s*)?['\\\"](?<g>[\\w.\\-]+):(?<a>[\\w.\\-]+):(?<v>[^'\\\"\\s)]+)['\\\"]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        internal static JavaInstalledSnapshot Read(string projectFile)
        {
            if (string.IsNullOrWhiteSpace(projectFile) || !File.Exists(projectFile))
                throw new IOException("Progetto Java non disponibile.");
            string folder = Path.GetDirectoryName(Path.GetFullPath(projectFile));
            var project = ReadXml(projectFile);
            string backend = project.Descendants()
                .Where(x => x.Name.LocalName == "JavaBuildSystem")
                .Select(x => x.Value.Trim()).FirstOrDefault() ?? "Plain";
            var list = new List<JavaInstalledDependency>();
            string note = null;
            if (string.Equals(backend, "Maven", StringComparison.OrdinalIgnoreCase))
            {
                string pom = Path.Combine(folder, "pom.xml");
                if (File.Exists(pom))
                {
                    XDocument pomXml = ReadXml(pom);
                    XElement root = pomXml.Root;
                    XNamespace ns = root.Name.Namespace;
                    XElement deps = root.Element(ns + "dependencies");
                    if (deps != null)
                    {
                        foreach (XElement dependency in deps.Elements(ns + "dependency"))
                        {
                            string g = (string)dependency.Element(ns + "groupId");
                            string a = (string)dependency.Element(ns + "artifactId");
                            string v = (string)dependency.Element(ns + "version");
                            if (!string.IsNullOrWhiteSpace(g) && !string.IsNullOrWhiteSpace(a))
                                list.Add(new JavaInstalledDependency(g.Trim(), a.Trim(),
                                    v == null ? "(gestita dal POM)" : v.Trim(), true,
                                    (string)dependency.Element(ns + "scope")));
                        }
                    }
                }
                note = "Solo dipendenze dirette del POM; escluse le transitive.";
            }
            else if (string.Equals(backend, "Gradle", StringComparison.OrdinalIgnoreCase))
            {
                string groovy = Path.Combine(folder, "build.gradle");
                string kotlin = Path.Combine(folder, "build.gradle.kts");
                string buildFile = File.Exists(groovy) ? groovy : kotlin;
                if (File.Exists(buildFile))
                {
                    string source = File.ReadAllText(buildFile);
                    foreach (Match match in GradleLiteral.Matches(source))
                        list.Add(new JavaInstalledDependency(match.Groups["g"].Value,
                            match.Groups["a"].Value, match.Groups["v"].Value, true,
                            JavaPackageScopes.FromGradle(match.Groups["config"].Value)));
                }
                note = "Dipendenze Gradle con coordinate letterali; alias Version Catalog, " +
                    "variabili e dipendenze transitive non sono ancora analizzati.";
            }
            else if (string.Equals(backend, "Plain", StringComparison.OrdinalIgnoreCase))
            {
                // F4F2112-PLAIN-MANAGEMENT: one entry per managed package,
                // even when Maven resolved several transitive JARs for it.
                foreach (XElement jar in project.Descendants()
                    .Where(x => x.Name.LocalName == "JavaJarReference"))
                {
                    string include = (string)jar.Attribute("Include");
                    if (string.IsNullOrWhiteSpace(include)) continue;
                    string id = jar.Elements().Where(x => x.Name.LocalName ==
                        JavaPlainPackageManager.ManagedId).Select(x => x.Value.Trim()).FirstOrDefault();
                    string version = jar.Elements().Where(x => x.Name.LocalName ==
                        JavaPlainPackageManager.ManagedVersion).Select(x => x.Value.Trim()).FirstOrDefault();
                    string scope = jar.Elements().Where(x => x.Name.LocalName ==
                        JavaPackageScopes.PlainMetadata).Select(x => x.Value.Trim()).FirstOrDefault();
                    string[] parts = (id ?? "").Split(':');
                    if (parts.Length == 2 && parts.All(x => x.Length > 0) &&
                        !string.IsNullOrWhiteSpace(version))
                        list.Add(new JavaInstalledDependency(parts[0], parts[1], version, true, scope));
                    else
                        list.Add(new JavaInstalledDependency("JAR esterno",
                            Path.GetFileName(include.Replace('\\', '/')), "-", false));
                }
                note = "Pacchetti Plain gestiti con coordinate Maven e JAR esterni manuali. " +
                    "Le dipendenze transitive sono raccolte sotto il pacchetto diretto.";
            }
            else note = "Backend non riconosciuto: elenco locale non disponibile.";
            return new JavaInstalledSnapshot(backend, list
                .GroupBy(x => x.Coordinate + ":" + x.Version + ":" + x.Scope, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First()).ToList(), note);
        }

        private static XDocument ReadXml(string path)
        {
            var settings = new XmlReaderSettings
            {
                XmlResolver = null, DtdProcessing = DtdProcessing.Prohibit,
                MaxCharactersInDocument = 8 * 1024 * 1024
            };
            using (var reader = XmlReader.Create(path, settings)) return XDocument.Load(reader);
        }
    }

    internal sealed class JavaInstalledSnapshot
    {
        internal JavaInstalledSnapshot(string backend, IList<JavaInstalledDependency> items, string note)
        {
            Backend = backend; Items = items; Note = note;
        }
        internal string Backend { get; private set; }
        internal IList<JavaInstalledDependency> Items { get; private set; }
        internal string Note { get; private set; }
    }

    internal sealed class JavaInstalledDependency
    {
        internal JavaInstalledDependency(string g, string a, string v, bool central, string scope = null)
        { GroupId = g; ArtifactId = a; Version = v; MavenCoordinate = central;
          Scope = JavaPackageScopes.Normalize(scope); }
        internal string GroupId { get; private set; }
        internal string ArtifactId { get; private set; }
        internal string Version { get; private set; }
        internal bool MavenCoordinate { get; private set; }
        internal string Scope { get; private set; }
        internal string Coordinate { get { return GroupId + ":" + ArtifactId; } }
        public override string ToString() { return Coordinate + "   " + Version + "   [" + Scope + "]"; }
    }

    internal sealed class JavaPackageUpdateCandidate
    {
        internal JavaPackageUpdateCandidate(JavaInstalledDependency installed, string latest)
        { Installed = installed; Latest = latest; }
        internal JavaInstalledDependency Installed { get; private set; }
        internal string Latest { get; private set; }
        public override string ToString()
        { return Installed.Coordinate + "   " + Installed.Version + "  →  " + Latest +
            "   [" + Installed.Scope + "]"; }
    }
}
