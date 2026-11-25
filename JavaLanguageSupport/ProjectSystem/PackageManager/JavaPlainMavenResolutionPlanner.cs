using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JavaLanguageSupport.ProjectSystem.PackageManager
{
    // JPM-S2D1-MAVEN-READONLY-20261124
    // Explicit, read-only Maven mediation simulation. The plan is never applied.
    // Temporary POM/tree files are outside the project; the existing JARs,
    // .javaproj, .classpath, .project and JDT LS workspace are not touched.
    internal static class JavaPlainMavenResolutionPlanner
    {
        internal sealed class Plan
        {
            internal string Details;
            internal int DirectCount;
            internal int CurrentJarCount;
            internal int SimulatedJarCount;
            internal int Changes;
            internal bool HasOverride;
            // JPM-S2D2-CLASSPATH-DIFF-20261124: snapshot of the installed Plain classpath vs
            // the resolved Maven proposal, not just two Maven graphs.
            internal IList<ClasspathDiff> ClasspathDiffs;
            internal string ComparisonDetails;
            internal int Added;
            internal int Updated;
            internal int Removed;
            internal int Unchanged;
            internal int Unverified;
            internal long ProjectStampTicks;
            internal long ProjectFileLength;
            // JPM-S2D31-TRANSACTION-PREFLIGHT-20261125: persist the exact Maven mediation request for a
            // fresh independent preflight re-resolution, never infer it from UI.
            internal string SelectedIdentity;
            internal string TargetVersion;
        }

        internal sealed class ClasspathDiff
        {
            // Public read-only presentation properties for WPF binding.
            public string Status { get; internal set; }
            public string Coordinate { get; internal set; }
            public string InstalledVersion { get; internal set; }
            public string ProposedVersion { get; internal set; }
            public string RequiredBy { get; internal set; }
            public string Scope { get; internal set; }
            public string StatusLabel { get { return Status == "Aggiunto" ? "+ Aggiunto" :
                Status == "Aggiornato" ? "~ Aggiornato" :
                Status == "Rimosso" ? "- Rimosso" : "= Invariato"; } }
        }

        private sealed class Node
        {
            internal string Group;
            internal string Artifact;
            internal string Version;
            internal string Scope;
            internal string Type;
            internal readonly List<Node> Children = new List<Node>();
            internal string Id { get { return Group + ":" + Artifact; } }
        }

        // Reuse precisely the same 3.7.1 Maven dependency plugin as Plain's
        // copy-dependencies. JSON output is supported since plugin 3.7.0.
        // No artifacts are copied into the Java project by this runner.
        private const string MavenTreeRunner = @"
$ErrorActionPreference = 'Stop'
function Find-Maven {
    $root = $env:JAVA_CPS_PLAIN_ROOT
    $wrapper = Join-Path $root 'mvnw.cmd'
    if (Test-Path -LiteralPath $wrapper -PathType Leaf) { return $wrapper }
    $cmd = Get-Command 'mvn.cmd' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd) { return $cmd.Source }
    foreach ($homeDir in @($env:MAVEN_HOME, $env:M2_HOME)) {
        if ([string]::IsNullOrWhiteSpace($homeDir)) { continue }
        $candidate = Join-Path $homeDir 'bin\mvn.cmd'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    foreach ($base in @($env:ChocolateyInstall, (Join-Path $env:ProgramData 'chocolatey'))) {
        if ([string]::IsNullOrWhiteSpace($base)) { continue }
        $package = Join-Path $base 'lib\maven'
        if (-not (Test-Path -LiteralPath $package -PathType Container)) { continue }
        foreach ($dir in (@($package) + @(Get-ChildItem -LiteralPath $package -Directory -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName }))) {
            $candidate = Join-Path $dir 'bin\mvn.cmd'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
        }
    }
    throw 'Maven non trovato: impostare PATH/MAVEN_HOME oppure mvnw.cmd nel progetto.'
}
try {
    $launcher = Find-Maven
    & $launcher '-B' '-ntp' '-f' $env:JAVA_CPS_PLAIN_POM '-N' 'org.apache.maven.plugins:maven-dependency-plugin:3.7.1:tree' '-DoutputType=json' ('-DoutputFile=' + $env:JAVA_CPS_PLAIN_TREE)
    if ($LASTEXITCODE -ne 0) { throw ('Maven exit code ' + $LASTEXITCODE) }
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
";

        internal static async Task<Plan> CreateAsync(string projectFile,
            string selectedIdentity, string targetVersion, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(projectFile))
                throw new ArgumentException("Selezionare un progetto Java Plain.", "projectFile");
            projectFile = Path.GetFullPath(projectFile);
            if (!File.Exists(projectFile) ||
                !projectFile.EndsWith(".javaproj", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Il progetto .javaproj non e disponibile.");
            string root = Path.GetDirectoryName(projectFile);
            cancellationToken.ThrowIfCancellationRequested();
            var installed = JavaPackageManagerInstalledReader.Read(projectFile);
            if (!string.Equals(installed.Backend, "Plain", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("La pianificazione Maven S2D.1 e prevista solo per progetti Plain.");

            // One direct Maven coordinate per Plain owner; do not guess in the
            // presence of inconsistent direct declarations or variable versions.
            var declarations = installed.Items.Where(x => x.MavenCoordinate).ToList();
            if (declarations.Count == 0)
                throw new InvalidOperationException("Non sono presenti dipendenze dirette Plain gestite.");
            if (declarations.Count > 128)
                throw new InvalidDataException("Troppe dipendenze dirette per una simulazione sicura.");
            foreach (var dep in declarations)
            {
                RequireCoordinate(dep.GroupId, "groupId");
                RequireCoordinate(dep.ArtifactId, "artifactId");
                RequireVersion(dep.Version);
            }
            var duplicate = declarations.GroupBy(x => x.Coordinate, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(x => x.Count() != 1);
            if (duplicate != null)
                throw new InvalidDataException("Coordinate dichiarate con versioni/scope multipli: " + duplicate.Key);
            if (string.IsNullOrWhiteSpace(selectedIdentity) != string.IsNullOrWhiteSpace(targetVersion))
                throw new ArgumentException("Per simulare un allineamento selezionare identita e versione.");
            if (targetVersion != null) RequireVersion(targetVersion);
            string group = null, artifact = null;
            if (selectedIdentity != null)
            {
                // S2A identity is groupId.artifactId, not Maven's group:artifact.
                // Never split on '.' (both parts may contain dots). Resolve by
                // matching against the identifiers of installed managed JARs.
                var current = JavaPlainVersionConflictGuard.Audit(projectFile);
                if (!(current.Libraries ?? new List<JavaPlainVersionConflictGuard.Library>())
                    .Any(x => string.Equals(x.Identity, selectedIdentity,
                        StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Identita non presente nei JAR Plain installati: " + selectedIdentity);
            }
            var sources = JavaPackageSourceDiscovery.Discover(projectFile, "Plain", cancellationToken)
                .Sources.Where(JavaPackageMultiRepositorySearchService.IsEligible)
                .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First()).Take(12).ToList();
            if (sources.Count == 0)
                throw new InvalidOperationException("Nessun repository Maven HTTPS attivo per il progetto.");
            string stamp = File.GetLastWriteTimeUtc(projectFile).Ticks.ToString() + ":" +
                new FileInfo(projectFile).Length;
            string stage = Path.Combine(Path.GetTempPath(),
                "java-vsix-s2d1-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try
            {
                string script = Path.Combine(stage, "tree.ps1");
                File.WriteAllText(script, MavenTreeRunner, new UTF8Encoding(false));
                string currentPom = Path.Combine(stage, "current.xml");
                string currentJson = Path.Combine(stage, "current.json");
                WritePom(currentPom, declarations, sources, null, null, null);
                JavaJpmProjectLogger.Info(projectFile, "JPM S2D.1",
                    "Risoluzione grafo Maven attuale: " + declarations.Count + " dipendenze dirette.");
                Node currentTree = await ExecuteTreeAsync(projectFile, root, script,
                    currentPom, currentJson, cancellationToken).ConfigureAwait(false);
                Node proposedTree = currentTree;
                if (selectedIdentity != null)
                {
                    // Locate exact G:A in the resolved Maven graph. The S2A
                    // display identity alone is ambiguous if a dot occurs in G/A.
                    var matches = Flatten(currentTree).Skip(1).Where(x =>
                        string.Equals(x.Group + "." + x.Artifact, selectedIdentity,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(x.Type, "jar", StringComparison.OrdinalIgnoreCase))
                        .Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (matches.Count != 1)
                        throw new InvalidOperationException("Identita non risolta univocamente dal grafo Maven: " + selectedIdentity);
                    string[] parts = matches[0].Split(new[] { ':' }, 2);
                    group = parts[0]; artifact = parts[1];
                    string desiredPom = Path.Combine(stage, "proposed.xml");
                    string desiredJson = Path.Combine(stage, "proposed.json");
                    WritePom(desiredPom, declarations, sources, group, artifact, targetVersion);
                    JavaJpmProjectLogger.Info(projectFile, "JPM S2D.1",
                        "Simulazione dependencyManagement " + matches[0] + ":" + targetVersion + ".");
                    proposedTree = await ExecuteTreeAsync(projectFile, root, script,
                        desiredPom, desiredJson, cancellationToken).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                string now = File.GetLastWriteTimeUtc(projectFile).Ticks.ToString() + ":" +
                    new FileInfo(projectFile).Length;
                if (!string.Equals(stamp, now, StringComparison.Ordinal))
                    throw new IOException("Il .javaproj e cambiato durante la simulazione: ripetere l'analisi.");
                Plan result = MakePlan(currentTree, proposedTree, declarations, projectFile,
                    selectedIdentity, targetVersion, sources.Count);
                var stampInfo = new FileInfo(projectFile);
                if (!stampInfo.Exists || stampInfo.LastWriteTimeUtc.Ticks.ToString() + ":" +
                    stampInfo.Length != stamp)
                    throw new IOException("Il .javaproj e cambiato durante la pianificazione.");
                result.ProjectStampTicks = stampInfo.LastWriteTimeUtc.Ticks;
                result.ProjectFileLength = stampInfo.Length;
                result.SelectedIdentity = selectedIdentity;
                result.TargetVersion = targetVersion;
                return result;
            }
            finally
            {
                try { Directory.Delete(stage, true); }
                catch (IOException) { /* Only temporary files. */ }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static void RequireCoordinate(string text, string label)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 160 ||
                !text.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_'))
                throw new InvalidDataException(label + " Maven non valido: " + text);
        }

        private static void RequireVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version) || version.Length > 120 ||
                !version.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' ||
                    c == '_' || c == '+'))
                throw new InvalidDataException("Versione Maven non supportata per una simulazione: " + version);
        }

        private static void Add(XmlDocument doc, XmlElement parent, string name, string value)
        {
            var element = doc.CreateElement(name, parent.NamespaceURI);
            element.InnerText = value;
            parent.AppendChild(element);
        }

        private static void WritePom(string path, IList<JavaInstalledDependency> dependencies,
            IList<JavaPackageSource> repositories, string group, string artifact, string version)
        {
            var doc = new XmlDocument();
            var root = doc.CreateElement("project", "http://maven.apache.org/POM/4.0.0");
            doc.AppendChild(root);
            Add(doc, root, "modelVersion", "4.0.0");
            Add(doc, root, "groupId", "java.vsix.plain");
            Add(doc, root, "artifactId", "jpm-s2d1-read-only-analysis");
            Add(doc, root, "version", "1.0.0");
            var repos = doc.CreateElement("repositories", root.NamespaceURI);
            root.AppendChild(repos);
            foreach (var item in repositories)
            {
                var repo = doc.CreateElement("repository", root.NamespaceURI);
                repos.AppendChild(repo);
                Add(doc, repo, "id", item.Id);
                Add(doc, repo, "url", item.Url.AbsoluteUri);
                var releases = doc.CreateElement("releases", root.NamespaceURI);
                repo.AppendChild(releases);
                Add(doc, releases, "enabled", item.Releases ? "true" : "false");
                var snapshots = doc.CreateElement("snapshots", root.NamespaceURI);
                repo.AppendChild(snapshots);
                Add(doc, snapshots, "enabled", item.Snapshots ? "true" : "false");
            }
            if (group != null)
            {
                var management = doc.CreateElement("dependencyManagement", root.NamespaceURI);
                root.AppendChild(management);
                var managed = doc.CreateElement("dependencies", root.NamespaceURI);
                management.AppendChild(managed);
                var item = doc.CreateElement("dependency", root.NamespaceURI);
                managed.AppendChild(item);
                Add(doc, item, "groupId", group);
                Add(doc, item, "artifactId", artifact);
                Add(doc, item, "version", version);
                Add(doc, item, "type", "jar");
            }
            var list = doc.CreateElement("dependencies", root.NamespaceURI);
            root.AppendChild(list);
            foreach (var dep in dependencies)
            {
                var item = doc.CreateElement("dependency", root.NamespaceURI);
                list.AppendChild(item);
                Add(doc, item, "groupId", dep.GroupId);
                Add(doc, item, "artifactId", dep.ArtifactId);
                Add(doc, item, "version", dep.Version);
                Add(doc, item, "scope", dep.Scope);
            }
            var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
            using (var writer = XmlWriter.Create(path, settings)) doc.Save(writer);
        }

        private static async Task<Node> ExecuteTreeAsync(string projectFile, string root,
            string script, string pom, string treeFile, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\"")
            {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.EnvironmentVariables["JAVA_CPS_PLAIN_ROOT"] = root;
            start.EnvironmentVariables["JAVA_CPS_PLAIN_POM"] = pom;
            start.EnvironmentVariables["JAVA_CPS_PLAIN_TREE"] = treeFile;
            using (var process = new Process { StartInfo = start })
            {
                if (!process.Start()) throw new IOException("Impossibile avviare PowerShell per Maven.");
                using (token.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(); }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }))
                {
                    // Drain both pipes concurrently, avoiding deadlocks on verbose tools.
                    Task<string> output = process.StandardOutput.ReadToEndAsync();
                    Task<string> errors = process.StandardError.ReadToEndAsync();
                    await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
                    string stdout = await output.ConfigureAwait(false);
                    string stderr = await errors.ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0)
                        throw new IOException("Maven dependency:tree non riuscito (exit " +
                            process.ExitCode + "): " + Tail(stderr + "\n" + stdout, 700));
                }
            }
            token.ThrowIfCancellationRequested();
            var file = new FileInfo(treeFile);
            if (!file.Exists || file.Length == 0 || file.Length > 8 * 1024 * 1024)
                throw new InvalidDataException("Il JSON del grafo Maven e assente o fuori limite (8 MB).");
            using (var reader = File.OpenText(treeFile))
            using (var json = new JsonTextReader(reader) { MaxDepth = 90 })
            {
                JObject document = JObject.Load(json);
                int count = 0;
                return Parse(document, 0, ref count);
            }
        }

        private static string Tail(string value, int limit)
        {
            value = (value ?? "").Replace('\r', ' ').Trim();
            return value.Length <= limit ? value : value.Substring(value.Length - limit);
        }

        private static Node Parse(JObject obj, int depth, ref int count)
        {
            if (++count > 6000 || depth > 72)
                throw new InvalidDataException("Grafo Maven troppo profondo o esteso per il planner S2D.1.");
            var node = new Node {
                Group = (string)obj["groupId"] ?? "",
                Artifact = (string)obj["artifactId"] ?? "",
                Version = (string)obj["version"] ?? "",
                Scope = (string)obj["scope"] ?? "",
                Type = (string)obj["type"] ?? ""
            };
            if (node.Group.Length == 0 || node.Artifact.Length == 0 || node.Version.Length == 0)
                throw new InvalidDataException("Nodo Maven JSON senza coordinate complete.");
            JArray children = obj["children"] as JArray;
            if (children != null)
            {
                foreach (JObject child in children.OfType<JObject>())
                    node.Children.Add(Parse(child, depth + 1, ref count));
            }
            return node;
        }

        private static IEnumerable<Node> Flatten(Node node)
        {
            yield return node;
            foreach (Node child in node.Children)
                foreach (Node leaf in Flatten(child)) yield return leaf;
        }

        private static Dictionary<string, string> ResolvedJars(Node graph)
        {
            var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in Flatten(graph).Skip(1).Where(n =>
                string.Equals(n.Type, "jar", StringComparison.OrdinalIgnoreCase)))
            {
                string old;
                if (results.TryGetValue(node.Id, out old) &&
                    !string.Equals(old, node.Version, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Il grafo Maven contiene versioni non univoche per " + node.Id);
                results[node.Id] = node.Version;
            }
            return results;
        }

        private static Plan MakePlan(Node current, Node proposed,
            IList<JavaInstalledDependency> declarations, string projectFile,
            string identity, string version, int sourceCount)
        {
            var from = ResolvedJars(current);
            var to = ResolvedJars(proposed);
            var audit = JavaPlainVersionConflictGuard.Audit(projectFile);
            var present = (audit.Libraries ?? new List<JavaPlainVersionConflictGuard.Library>())
                .GroupBy(x => x.Identity, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(x => x.Version)
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x,
                        StringComparer.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase);
            var lines = new StringBuilder();
            lines.AppendLine("JPM-S2D.1 | PIANO MAVEN IN SOLA LETTURA");
            lines.AppendLine("Stato: SIMULAZIONE, nessuna modifica al progetto.");
            lines.AppendLine("Radici Maven dirette: " + declarations.Count +
                "; repository HTTPS: " + sourceCount + ".");
            lines.AppendLine("Grafo Maven attuale: " + from.Count + " JAR risolti.");
            lines.AppendLine("JAR Plain gestiti verificati: " + (audit.Libraries == null ? 0 : audit.Libraries.Count) +
                "; non verificabili: " + audit.UnverifiedCount + ".");
            var deviations = from.Where(kv =>
                !present.TryGetValue(kv.Key.Replace(':', '.'), out string installed) ||
                !string.Equals(installed, kv.Value, StringComparison.OrdinalIgnoreCase)).ToList();
            lines.AppendLine("Differenze rispetto ai JAR Plain attuali: " + deviations.Count +
                " (Maven media le versioni tra tutte le radici dirette).");
            foreach (var item in deviations.Take(18))
            {
                present.TryGetValue(item.Key.Replace(':', '.'), out string installed);
                lines.AppendLine("  ~ " + item.Key + ": attuale=" + (installed ?? "assente") +
                    ", Maven=" + item.Value);
            }
            if (deviations.Count > 18) lines.AppendLine("  ... altre " + (deviations.Count - 18) + " differenze.");
            int changes = 0;
            if (identity != null)
            {
                lines.AppendLine();
                lines.AppendLine("IPOTESI DI ALLINEAMENTO: " + identity + " -> " + version);
                lines.AppendLine("Tecnica: dependencyManagement nel POM TEMPORANEO (mai nel progetto).");
                var effective = Flatten(proposed).Skip(1).FirstOrDefault(n =>
                    string.Equals(n.Group + "." + n.Artifact, identity,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(n.Type, "jar", StringComparison.OrdinalIgnoreCase));
                if (effective == null || !string.Equals(effective.Version, version,
                    StringComparison.OrdinalIgnoreCase))
                    lines.AppendLine("AVVISO: versione richiesta non presente nel grafo Maven " +
                        "risolto; dipendenze dirette e mediazione possono prevalere.");
                var keys = new HashSet<string>(from.Keys, StringComparer.OrdinalIgnoreCase);
                keys.UnionWith(to.Keys);
                foreach (string key in keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    from.TryGetValue(key, out string oldVersion);
                    to.TryGetValue(key, out string newVersion);
                    if (string.Equals(oldVersion, newVersion, StringComparison.OrdinalIgnoreCase)) continue;
                    changes++;
                    if (changes <= 40)
                        lines.AppendLine("  " + (oldVersion == null ? "+ " : newVersion == null ? "- " : "~ ") +
                            key + ": " + (oldVersion ?? "assente") + " -> " + (newVersion ?? "assente"));
                }
                if (changes > 40) lines.AppendLine("  ... altre " + (changes - 40) + " modifiche.");
                lines.AppendLine("JAR coinvolti dal piano: " + changes + ".");
            }
            else
            {
                lines.AppendLine();
                lines.AppendLine("Nessuna versione selezionata: questa e la ricostruzione Maven di base.");
                lines.AppendLine("Seleziona un conflitto e una versione per confrontare due grafi risolti.");
            }
            lines.AppendLine();
            lines.AppendLine("ALBERO MAVEN EFFETTIVO (prima dei cambiamenti):");
            int shown = 0;
            AppendTree(lines, current, 0, ref shown);
            if (shown >= 130) lines.AppendLine("  ... albero abbreviato a 130 nodi.");
            if (identity != null)
            {
                lines.AppendLine();
                lines.AppendLine("ALBERO MAVEN SIMULATO (dependencyManagement temporaneo):");
                int simulatedShown = 0;
                AppendTree(lines, proposed, 0, ref simulatedShown);
                if (simulatedShown >= 130)
                    lines.AppendLine("  ... albero simulato abbreviato a 130 nodi.");
            }
            lines.AppendLine();
            lines.AppendLine("ATTENZIONE: il modello Maven con radici aggregate e una simulazione.");
            lines.AppendLine("La selezione reale Plain risolve le radici separatamente; il risultato");
            lines.AppendLine("non dimostra compatibilita API/binaria, non modifica JAR, .javaproj,");
            lines.AppendLine(".classpath o JDT LS. Ri-risolvere prima di qualsiasi applicazione.");
            // S2D.2 compares the actual managed Plain jars with the proposed
            // mediated graph. Differences between two Maven graphs (Changes)
            // remain an independent S2D.1 metric.
            var comparison = CompareClasspath(audit, to, proposed);
            string comparisonDetails = FormatClasspathComparison(comparison, audit.UnverifiedCount);
            lines.AppendLine();
            lines.AppendLine(comparisonDetails);
            return new Plan {
                Details = lines.ToString().TrimEnd(), DirectCount = declarations.Count,
                CurrentJarCount = from.Count, SimulatedJarCount = to.Count,
                Changes = changes, HasOverride = identity != null,
                ComparisonDetails = comparisonDetails, ClasspathDiffs = comparison,
                Added = comparison.Count(x => x.Status == "Aggiunto"),
                Updated = comparison.Count(x => x.Status == "Aggiornato"),
                Removed = comparison.Count(x => x.Status == "Rimosso"),
                Unchanged = comparison.Count(x => x.Status == "Invariato"),
                Unverified = audit.UnverifiedCount
            };
        }

        // JPM-S2D2-CLASSPATH-DIFF-20261124: keep the roots in the Maven tree. They are
        // informative, not proof of runtime binary/API compatibility.
        private static Dictionary<string, List<string>> MavenOwners(Node graph)
        {
            var owners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (Node direct in graph.Children)
            {
                string root = direct.Id + ":" + direct.Version;
                foreach (Node item in Flatten(direct).Where(x =>
                    string.Equals(x.Type, "jar", StringComparison.OrdinalIgnoreCase)))
                {
                    string alias = item.Group + "." + item.Artifact;
                    if (!owners.TryGetValue(alias, out HashSet<string> names))
                    {
                        names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        owners.Add(alias, names);
                    }
                    names.Add(root);
                }
            }
            return owners.ToDictionary(x => x.Key,
                x => x.Value.OrderBy(y => y, StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);
        }

        private static List<ClasspathDiff> CompareClasspath(
            JavaPlainVersionConflictGuard.Report audit,
            Dictionary<string, string> proposed, Node graph)
        {
            var libraries = audit.Libraries ?? new List<JavaPlainVersionConflictGuard.Library>();
            var current = libraries.GroupBy(x => x.Identity,
                StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key,
                g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            // S2A identity uses groupId.artifactId, while Maven uses group:artifact.
            // Refuse the rare ambiguous dotted alias rather than merging unrelated
            // Maven artifacts (e.g. group a.b:c versus a:b.c).
            var future = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var resolved in proposed)
            {
                string alias = resolved.Key.Replace(':', '.');
                if (ids.TryGetValue(alias, out string other) &&
                    !string.Equals(other, resolved.Key, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Identita Maven ambigua per confronto Plain: " + alias);
                ids[alias] = resolved.Key;
                future[alias] = resolved.Value;
            }
            var owners = MavenOwners(graph);
            var all = new HashSet<string>(current.Keys, StringComparer.OrdinalIgnoreCase);
            all.UnionWith(future.Keys);
            var rows = new List<ClasspathDiff>();
            foreach (string identity in all)
            {
                current.TryGetValue(identity, out List<JavaPlainVersionConflictGuard.Library> actual);
                future.TryGetValue(identity, out string desired);
                var versions = (actual ?? new List<JavaPlainVersionConflictGuard.Library>())
                    .Select(x => x.Version).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                string state = versions.Count == 0 ? "Aggiunto" : desired == null ? "Rimosso" :
                    versions.Count == 1 && string.Equals(versions[0], desired,
                        StringComparison.OrdinalIgnoreCase) ? "Invariato" : "Aggiornato";
                string plainOwners = string.Join(", ", (actual ??
                    new List<JavaPlainVersionConflictGuard.Library>()).Select(x => x.Owner)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(12));
                owners.TryGetValue(identity, out List<string> mavenRoots);
                string mavenOwners = mavenRoots == null ? "" :
                    string.Join(", ", mavenRoots.Take(12));
                string scopes = string.Join(", ", (actual ??
                    new List<JavaPlainVersionConflictGuard.Library>()).Select(x => x.Scope)
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x,
                        StringComparer.OrdinalIgnoreCase));
                rows.Add(new ClasspathDiff {
                    Status = state,
                    Coordinate = ids.TryGetValue(identity, out string coordinate) ? coordinate : identity,
                    InstalledVersion = versions.Count == 0 ? "—" : string.Join(" / ", versions),
                    ProposedVersion = desired ?? "—",
                    RequiredBy = "Plain: " + (plainOwners.Length == 0 ? "—" : plainOwners) +
                        " | Maven: " + (mavenOwners.Length == 0 ? "—" : mavenOwners),
                    Scope = scopes.Length == 0 ? "—" : scopes
                });
            }
            var order = new Dictionary<string, int>(StringComparer.Ordinal) {
                { "Aggiornato", 0 }, { "Aggiunto", 1 }, { "Rimosso", 2 }, { "Invariato", 3 }
            };
            return rows.OrderBy(x => order[x.Status]).ThenBy(x => x.Coordinate,
                StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string FormatClasspathComparison(
            IList<ClasspathDiff> rows, int unverified)
        {
            var text = new StringBuilder();
            text.AppendLine("JPM-S2D.2 | CONFRONTO CLASSPATH - SOLA LETTURA");
            text.AppendLine("Attuale = JAR Plain verificati; Proposto = grafo Maven mediato.");
            text.AppendLine("Aggiunti: " + rows.Count(x => x.Status == "Aggiunto") +
                "; aggiornati: " + rows.Count(x => x.Status == "Aggiornato") +
                "; rimossi: " + rows.Count(x => x.Status == "Rimosso") +
                "; invariati: " + rows.Count(x => x.Status == "Invariato") + ".");
            if (unverified > 0)
                text.AppendLine("ATTENZIONE: " + unverified +
                    " JAR non verificabili non sono classificati nella tabella.");
            foreach (var row in rows.Take(240))
                text.AppendLine("  [" + row.Status + "] " + row.Coordinate +
                    ": " + row.InstalledVersion + " -> " + row.ProposedVersion +
                    " [" + row.Scope + "] " + row.RequiredBy);
            if (rows.Count > 240)
                text.AppendLine("  ... altre " + (rows.Count - 240) +
                    " righe consultabili nella tabella del Package Manager.");
            text.AppendLine("Differenze simulate; alcune versioni dirette possono prevalere su dependencyManagement.");
            text.AppendLine("Non dimostra compatibilita binaria o sicurezza della rimozione dei JAR.");
            text.AppendLine("Nessun JAR, .javaproj, .classpath o workspace JDT LS modificato.");
            return text.ToString().TrimEnd();
        }

        private static void AppendTree(StringBuilder output, Node node, int depth, ref int count)
        {
            if (count >= 130 || depth > 16) return;
            count++;
            output.Append(' ', Math.Min(depth, 16) * 2)
                .Append(node.Id).Append(':').Append(node.Version)
                .Append(" [").Append(node.Scope).AppendLine("]");
            foreach (var child in node.Children)
                AppendTree(output, child, depth + 1, ref count);
        }
    }
}
