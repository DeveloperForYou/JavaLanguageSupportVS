using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace JavaLanguageSupport.ProjectSystem.PackageManager
{
    // Discovery only. Credentials and repository queries are deliberately delegated to a later stage.
    internal sealed class JavaPackageSource
    {
        internal string Id { get; set; }
        internal string Name { get; set; }
        internal Uri Url { get; set; }
        internal string Origin { get; set; }
        internal string ServerId { get; set; }
        internal bool HasServerConfiguration { get; set; }
        internal bool Releases { get; set; } = true;
        internal bool Snapshots { get; set; }
        internal bool Blocked { get; set; }
        internal string Layout { get; set; } = "default";
        internal string Display()
        {
            return Id + " · " + (Url == null ? "URL non supportato" : Url.AbsoluteUri) +
                " [" + Origin + "]" + (Blocked ? " · bloccato/non utilizzabile" : "") +
                (HasServerConfiguration ? " · server Maven configurato" : "") +
                (Snapshots && !Releases ? " · solo snapshot" : !Snapshots && Releases ? " · release" : "");
        }
    }

    internal sealed class JavaPackageSourceSnapshot
    {
        internal List<JavaPackageSource> Sources { get; } = new List<JavaPackageSource>();
        internal List<string> Warnings { get; } = new List<string>();
        internal string Describe()
        {
            var lines = Sources.Select(x => x.Display()).ToList();
            if (lines.Count == 0) lines.Add("Nessuna origine Maven rilevata.");
            lines.AddRange(Warnings.Distinct().Select(w => "Nota: " + w));
            return string.Join("\n", lines);
        }
    }

    internal static class JavaPackageSourceDiscovery
    {
        private const string Central = "https://repo.maven.apache.org/maven2/";
        private const int MaxXml = 4 * 1024 * 1024;

        internal static JavaPackageSourceSnapshot Discover(string projectFile, string backend, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string root = Path.GetDirectoryName(Path.GetFullPath(projectFile));
            if (string.Equals(backend, "Gradle", StringComparison.OrdinalIgnoreCase))
                return DiscoverGradle(root, projectFile, token);
            if (!string.Equals(backend, "Maven", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(backend, "Plain", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Backend non riconosciuto");
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string user = Path.Combine(home, ".m2", "settings.xml");
            string mavenHome = Environment.GetEnvironmentVariable("MAVEN_HOME");
            if (string.IsNullOrWhiteSpace(mavenHome)) mavenHome = Environment.GetEnvironmentVariable("M2_HOME");
            if (string.IsNullOrWhiteSpace(mavenHome))
            {
                foreach (string item in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                {
                    try
                    {
                        var bin = item.Trim().Trim('"');
                        if (!File.Exists(Path.Combine(bin, "mvn.cmd"))) continue;
                        var parent = Directory.GetParent(bin);
                        if (parent != null && File.Exists(Path.Combine(parent.FullName, "conf", "settings.xml")))
                        { mavenHome = parent.FullName; break; }
                    }
                    catch (ArgumentException) { }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            string global = string.IsNullOrWhiteSpace(mavenHome) ? null : Path.Combine(mavenHome, "conf", "settings.xml");
            var result = ReadMaven(global, user, string.Equals(backend, "Maven", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(root, "pom.xml") : null, token);
            if (File.Exists(Path.Combine(root, ".mvn", "maven.config")))
                result.Warnings.Add(".mvn/maven.config presente: opzioni/profili runtime non valutati.");
            if (global == null || !File.Exists(global))
                result.Warnings.Add("Settings globale non rilevato tramite MAVEN_HOME/M2_HOME/PATH.");
            return result;
        }

        internal static JavaPackageSourceSnapshot ReadMaven(string globalFile, string userFile, string pomFile, CancellationToken token)
        {
            var result = new JavaPackageSourceSnapshot();
            var global = ReadXml(globalFile, "settings", result, "settings globale");
            var user = ReadXml(userFile, "settings", result, "settings utente");
            var pom = ReadXml(pomFile, "project", result, "pom.xml");
            token.ThrowIfCancellationRequested();
            var active = new HashSet<string>(Children(Child(user, "activeProfiles"), "activeProfile")
                .Concat(Children(Child(global, "activeProfiles"), "activeProfile"))
                .Select(x => x.Value.Trim()), StringComparer.Ordinal);
            var servers = new HashSet<string>(MergeById(Child(user, "servers"), Child(global, "servers"), "server")
                .Select(x => Value(x, "id")), StringComparer.Ordinal);
            var mirrors = MergeById(Child(user, "mirrors"), Child(global, "mirrors"), "mirror").ToList();
            var settingsProfiles = MergeById(Child(user, "profiles"), Child(global, "profiles"), "profile").ToList();
            var raw = new List<JavaPackageSource>();
            AddProfiles(raw, settingsProfiles, active, "settings.xml", result);
            AddProfiles(raw, Children(Child(pom, "profiles"), "profile").ToList(), active, "pom.xml (profilo)", result);
            AddRepositories(raw, Child(pom, "repositories"), "pom.xml", result);
            if (Child(pom, "parent") != null)
                result.Warnings.Add("POM padre presente: repository ereditati non valutati in questa fase.");
            raw.Add(new JavaPackageSource { Id = "central", Name = "Maven Central", Url = new Uri(Central), Origin = "Maven Central" });
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in raw.GroupBy(x => x.Id, StringComparer.Ordinal).Select(x => x.First()))
            {
                token.ThrowIfCancellationRequested();
                var mirror = mirrors.FirstOrDefault(m => MirrorLayout(m, source) && Value(m, "mirrorOf") == source.Id)
                    ?? mirrors.FirstOrDefault(m => MirrorLayout(m, source) && MatchesMirror(Value(m, "mirrorOf"), source));
                if (mirror != null)
                {
                    source.Id = Value(mirror, "id");
                    source.Name = Value(mirror, "name");
                    source.Url = SafeUrl(Value(mirror, "url"));
                    source.Origin = "mirror di " + source.Origin;
                    source.Blocked = IsTrue(Value(mirror, "blocked")) || source.Url == null;
                }
                source.ServerId = source.Id;
                source.HasServerConfiguration = servers.Contains(source.Id);
                source.Blocked |= source.Url == null || source.Url.Scheme != Uri.UriSchemeHttps || source.Layout != "default";
                if (seen.Add(source.Id)) result.Sources.Add(source);
                else
                {
                    var previous = result.Sources.First(x => x.Id == source.Id);
                    if (Equals(previous.Url, source.Url)) { previous.Releases |= source.Releases; previous.Snapshots |= source.Snapshots; }
                    else { previous.Blocked = true; result.Warnings.Add("ID repository duplicato con URL differenti; origine bloccata."); }
                }
            }
            return result;
        }

        private static XElement ReadXml(string file, string expected, JavaPackageSourceSnapshot result, string label)
        {
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return null;
            try
            {
                if (new FileInfo(file).Length > MaxXml) throw new InvalidDataException();
                using (var stream = File.OpenRead(file))
                using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXml }))
                {
                    var root = XDocument.Load(reader).Root;
                    if (root == null || root.Name.LocalName != expected) throw new InvalidDataException();
                    return root;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is XmlException)
            {
                result.Warnings.Add(label + " non leggibile: rilevamento parziale.");
                return null;
            }
        }

        private static XElement Child(XElement node, string name) => Children(node, name).FirstOrDefault();
        private static IEnumerable<XElement> Children(XElement node, string name) => node == null ? Enumerable.Empty<XElement>() : node.Elements().Where(x => x.Name.LocalName == name);
        private static string Value(XElement node, string name) => (Child(node, name)?.Value ?? "").Trim();
        private static bool IsTrue(string value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        private static IEnumerable<XElement> MergeById(XElement dominant, XElement fallback, string tag) => Children(dominant, tag).Concat(Children(fallback, tag)).Where(x => Value(x, "id").Length > 0).GroupBy(x => Value(x, "id"), StringComparer.Ordinal).Select(x => x.First());

        private static void AddProfiles(List<JavaPackageSource> raw, IList<XElement> profiles, HashSet<string> active, string origin, JavaPackageSourceSnapshot result)
        {
            bool explicitActive = profiles.Any(x => active.Contains(Value(x, "id")));
            bool conditional = profiles.Any(x => Children(Child(x, "activation"), "jdk").Any() || Children(Child(x, "activation"), "os").Any() || Children(Child(x, "activation"), "property").Any() || Children(Child(x, "activation"), "file").Any());
            if (conditional) result.Warnings.Add("Attivazione condizionale Maven non valutata: elenco parziale per " + origin + ".");
            foreach (var profile in profiles.Reverse())
                if (active.Contains(Value(profile, "id")) || (!explicitActive && !conditional && IsTrue(Value(Child(profile, "activation"), "activeByDefault"))))
                    AddRepositories(raw, Child(profile, "repositories"), origin, result);
        }

        private static void AddRepositories(List<JavaPackageSource> raw, XElement repositories, string origin, JavaPackageSourceSnapshot result)
        {
            foreach (var repository in Children(repositories, "repository"))
            {
                string id = Value(repository, "id");
                if (id.Length == 0) continue;
                var url = SafeUrl(Value(repository, "url"));
                if (url == null) result.Warnings.Add("Repository con URL non letterale/non supportato in " + origin + ".");
                string layout = Value(repository, "layout");
                raw.Add(new JavaPackageSource { Id = id, Name = Value(repository, "name"), Url = url, Origin = origin,
                    Layout = layout.Length == 0 ? "default" : layout,
                    Releases = Value(Child(repository, "releases"), "enabled") != "false",
                    Snapshots = Value(Child(repository, "snapshots"), "enabled") != "false" });
            }
        }

        // No embedded credentials, query tokens, fragments, nonliteral interpolation or local files.
        internal static Uri SafeUrl(string value)
        {
            Uri url;
            if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace) || value.Contains("${") || !Uri.TryCreate(value, UriKind.Absolute, out url) ||
                (url.Scheme != "https" && url.Scheme != "http") || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0) return null;
            return url;
        }

        private static bool MirrorLayout(XElement mirror, JavaPackageSource source)
        {
            string layout = Value(mirror, "mirrorOfLayouts");
            return layout.Length == 0 || Matches(layout, source.Layout, false, false);
        }
        private static bool MatchesMirror(string pattern, JavaPackageSource source)
        {
            bool external = source.Url != null && !source.Url.IsLoopback;
            return Matches(pattern, source.Id, external, external && source.Url.Scheme == "http");
        }
        private static bool Matches(string pattern, string id, bool external, bool externalHttp)
        {
            var parts = pattern.Split(',').Select(x => x.Trim()).ToArray();
            if (parts.Contains("!" + id)) return false;
            return parts.Contains(id) || parts.Contains("*") || (external && parts.Contains("external:*")) || (externalHttp && parts.Contains("external:http:*"));
        }

        // The init script runs only on explicit request, in the actual Gradle model, offline.
        internal static JavaPackageSourceSnapshot DiscoverGradle(string root, string projectFile, CancellationToken token)
        {
            var result = new JavaPackageSourceSnapshot();
            var temp = Path.Combine(Path.GetTempPath(), "java-cps-origins-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            string statusFile = Path.Combine(temp, "status.txt");
            try
            {
                string init = Path.Combine(temp, "origins.init.gradle");
                string runner = Path.Combine(temp, "origins.ps1");
                string output = Path.Combine(temp, "origins.json");
                File.WriteAllText(init, GradleInit, new UTF8Encoding(false));
                File.WriteAllText(runner, GradleRunner, new UTF8Encoding(true));
                string command = null;
                var project = ReadXml(projectFile, "Project", result, "javaproj");
                if (project != null)
                    command = project.Elements().Where(x => x.Name.LocalName == "PropertyGroup" && x.Attribute("Condition") == null)
                        .SelectMany(x => Children(x, "JavaGradleCommand")).Where(x => x.Attribute("Condition") == null).Select(x => x.Value.Trim()).LastOrDefault();
                if (!string.IsNullOrEmpty(command) && command.Contains("$(")) { command = null; result.Warnings.Add("JavaGradleCommand dinamico: rilevamento tramite wrapper/PATH."); }
                var start = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + runner + "\"")
                { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.EnvironmentVariables["JAVA_CPS_ORIGIN_ROOT"] = root;
                start.EnvironmentVariables["JAVA_CPS_ORIGIN_COMMAND"] = command ?? "";
                start.EnvironmentVariables["JAVA_CPS_ORIGIN_INIT"] = init;
                start.EnvironmentVariables["JAVA_CPS_ORIGIN_OUTPUT"] = output;
                start.EnvironmentVariables["JAVA_CPS_ORIGIN_STATUS"] = statusFile;
                using (var process = new Process { StartInfo = start })
                {
                    process.OutputDataReceived += (s, e) => { };
                    process.ErrorDataReceived += (s, e) => { };
                    token.ThrowIfCancellationRequested();
                    if (!process.Start()) throw new InvalidOperationException();
                    process.BeginOutputReadLine(); process.BeginErrorReadLine();
                    var clock = Stopwatch.StartNew();
                    try
                    {
                        while (!process.WaitForExit(200))
                        {
                            token.ThrowIfCancellationRequested();
                            if (clock.Elapsed.TotalMinutes > 3) throw new TimeoutException();
                        }
                        token.ThrowIfCancellationRequested();
                        if (process.ExitCode != 0) throw new InvalidOperationException();
                    }
                    catch { KillTree(process); throw; }
                }
                if (!File.Exists(output) || new FileInfo(output).Length > MaxXml) throw new InvalidDataException();
                var json = JObject.Parse(File.ReadAllText(output, Encoding.UTF8));
                var repos = json["repositories"] as JArray;
                if (repos == null) throw new InvalidDataException();
                foreach (var repo in repos.OfType<JObject>())
                {
                    token.ThrowIfCancellationRequested();
                    var url = SafeUrl((string)repo["url"]);
                    if (url == null) { result.Warnings.Add("Origine Gradle locale/non HTTP(S) o URL con credenziali ignorato."); continue; }
                    string id = (string)repo["name"] ?? "maven";
                    if (result.Sources.Any(x => x.Id == id && Equals(x.Url, url))) continue;
                    string scope = (string)repo["scope"] ?? "project";
                    bool effective = (bool?)repo["effective"] ?? true;
                    string origin = "Gradle: " + scope + (effective ? " (attivo)" : " (non attivo per le dipendenze)");
                    result.Sources.Add(new JavaPackageSource { Id = id, Name = id, Url = url,
                        Origin = origin, Blocked = url.Scheme != "https", Releases = true, Snapshots = true });
                }
                if ((bool?)json["otherRepositories"] == true) result.Warnings.Add("Repository non Maven o con URL non supportato esclusi dal catalogo.");
                if (repos.OfType<JObject>().Any(r => (string)r["scope"] == "settings" && (bool?)r["effective"] == false))
                    result.Warnings.Add("Gradle usa PREFER_PROJECT: i repository in settings.gradle sono configurati ma non usati per le dipendenze, perche il progetto dichiara repository. Impostare PREFER_SETTINGS oppure rimuovere il blocco repositories dal build.gradle.");
                if (((int?)json["pluginOnlyCount"] ?? 0) > 0)
                    result.Warnings.Add("I repository in pluginManagement servono solo per i plugin Gradle e non sono mostrati come origini di librerie.");
                result.Warnings.Add("Filtri di contenuto Gradle e autenticazione restano gestiti da Gradle; non valutati per le ricerche in questa fase.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is TimeoutException || ex is System.ComponentModel.Win32Exception || ex is Newtonsoft.Json.JsonException)
            {
                string reason = "errore non classificato";
                try
                {
                    if (File.Exists(statusFile))
                    {
                        string code = File.ReadAllText(statusFile).Trim();
                        if (code == "launcher-not-found") reason = "Gradle non trovato (gradle.exe/gradle.bat, PATH, GRADLE_HOME, Chocolatey, wrapper)";
                        else if (code.StartsWith("gradle-exit-", StringComparison.Ordinal))
                            reason = "Gradle terminato con codice " + code.Substring("gradle-exit-".Length) +
                                " (verificare cache plugin e build in modalità offline)";
                        else if (code == "no-probe-output") reason = "Gradle non ha prodotto origini per il progetto selezionato";
                        else if (code == "runner-error") reason = "errore del launcher PowerShell";
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                result.Warnings.Add("Rilevamento Gradle non riuscito: " + reason + ". Nessuna origine inventata.");
            }
            finally { try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            return result;
        }

        private static void KillTree(Process process)
        {
            try
            {
                if (process.HasExited) return;
                using (var killer = Process.Start(new ProcessStartInfo("taskkill.exe", "/PID " + process.Id + " /T /F") { CreateNoWindow = true, UseShellExecute = false }))
                    if (killer != null) killer.WaitForExit(3000);
            }
            catch { try { if (!process.HasExited) process.Kill(); } catch { } }
        }

        // F4F281-GRADLE-ORIGINS: reuse the same launcher locations as CPS Gradle build.
        // No credentials or raw Gradle output are written to the UI.
        // F4F282-GRADLE-LAUNCHER: avoid automatic $HOME; support Chocolatey gradle.exe.
        private const string GradleRunner = @"
$ErrorActionPreference = 'Stop'
$root = $env:JAVA_CPS_ORIGIN_ROOT
$requested = $env:JAVA_CPS_ORIGIN_COMMAND
function Status([string]$reason) {
    [IO.File]::WriteAllText($env:JAVA_CPS_ORIGIN_STATUS, $reason)
}
function Launcher([string]$candidate) {
    if ([string]::IsNullOrWhiteSpace($candidate)) { return $null }
    try {
        $full = [IO.Path]::GetFullPath($candidate)
        if ([IO.File]::Exists($full)) {
            $ext = [IO.Path]::GetExtension($full)
            $name = [IO.Path]::GetFileName($full)
            if ($ext -in @('.bat','.cmd') -or ($ext -eq '.exe' -and $name -eq 'gradle.exe')) { return $full }
        }
    } catch { }
    return $null
}
function Resolve-Gradle {
    if (-not [string]::IsNullOrWhiteSpace($requested)) {
        $candidate = $requested.Trim().Trim([char]34)
        $direct = Launcher $candidate
        if (-not $direct) { $direct = Launcher (Join-Path $root $candidate) }
        if ($direct) { return $direct }
        if ($candidate -notin @('gradle','gradle.bat','gradle.cmd','gradle.exe')) { return $null }
    } else {
        $dir = Get-Item -LiteralPath $root
        while ($null -ne $dir) {
            $wrapper = Launcher (Join-Path $dir.FullName 'gradlew.bat')
            if ($wrapper) { return $wrapper }
            if ((Test-Path (Join-Path $dir.FullName 'settings.gradle')) -or
                (Test-Path (Join-Path $dir.FullName 'settings.gradle.kts'))) { break }
            $dir = $dir.Parent
        }
    }
    $names = if ([string]::IsNullOrWhiteSpace($requested)) {
        @('gradle.bat','gradle.cmd','gradle.exe','gradle')
    } else { @($requested.Trim().Trim([char]34)) }
    foreach ($name in $names) {
        $app = Get-Command -Name $name -CommandType Application -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($app) {
            $found = Launcher $app.Source
            if ($found) { return $found }
        }
    }
    # Never assign to PowerShell's case-insensitive read-only automatic $HOME.
    $gradleInstallHome = $env:GRADLE_HOME
    if (-not [string]::IsNullOrWhiteSpace($gradleInstallHome)) {
        foreach ($launcherFile in @('gradle.bat','gradle.cmd','gradle.exe')) {
            $found = Launcher (Join-Path $gradleInstallHome ('bin\' + $launcherFile))
            if ($found) { return $found }
        }
    }
    # Chocolatey may install Gradle without publishing its path to devenv.exe.
    $bases = @($env:ChocolateyInstall, (Join-Path $env:ProgramData 'chocolatey')) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
    foreach ($base in $bases) {
        # Chocolatey shim is often an .exe in ProgramData\chocolatey\bin.
        $shim = Launcher (Join-Path $base 'bin\gradle.exe')
        if ($shim) { return $shim }
        foreach ($package in @('gradle','gradle.portable')) {
            $dir = Join-Path $base ('lib\' + $package)
            if (-not (Test-Path -LiteralPath $dir -PathType Container)) { continue }
            $dirs = @($dir) + @(
                Get-ChildItem -LiteralPath $dir -Directory -ErrorAction SilentlyContinue |
                    Sort-Object Name -Descending | ForEach-Object { $_.FullName })
            foreach ($p in $dirs) {
                $found = Launcher (Join-Path $p 'bin\gradle.bat')
                if ($found) { return $found }
                $tools = Join-Path $p 'tools'
                if (-not (Test-Path -LiteralPath $tools -PathType Container)) { continue }
                $found = Launcher (Join-Path $tools 'bin\gradle.bat')
                if ($found) { return $found }
                foreach ($version in (Get-ChildItem -LiteralPath $tools -Directory -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name -match '^gradle-[0-9]' } | Sort-Object Name -Descending)) {
                    $found = Launcher (Join-Path $version.FullName 'bin\gradle.bat')
                    if ($found) { return $found }
                }
            }
        }
    }
    return $null
}
try {
    $exe = Resolve-Gradle
    if (-not $exe) { Status 'launcher-not-found'; exit 2 }
    # `help` exists on normal Gradle builds. The init script writes the catalog
    # during projectsEvaluated; no dynamically registered custom task is needed.
    # Windows PowerShell 5.1 can promote native stderr to ErrorRecord.
    # The Gradle process exit code, not its stderr, decides probe success.
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $exe '-p' $root '--offline' '--no-daemon' '--console=plain' '-Dorg.gradle.configuration-cache=false' '-I' $env:JAVA_CPS_ORIGIN_INIT 'help' 2>&1 | Out-Null
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $previousErrorActionPreference }
    if ($null -eq $code -or $code -ne 0) {
        Status ('gradle-exit-' + $code)
        exit 3
    }
    if (-not (Test-Path -LiteralPath $env:JAVA_CPS_ORIGIN_OUTPUT -PathType Leaf)) {
        Status 'no-probe-output'
        exit 4
    }
    Status 'ok'
    exit 0
} catch {
    Status 'runner-error'
    exit 5
}
";

        // F4F283-GRADLE-SETTINGS: enumerate both declared scopes, not only effective scope.
        private const string GradleInit = @"
import groovy.json.JsonOutput
import org.gradle.api.artifacts.repositories.MavenArtifactRepository
import org.gradle.util.GradleVersion

def settingsModel = null
gradle.settingsEvaluated { settingsModel = it }
gradle.projectsEvaluated {
    def targetDir = new File(System.getenv('JAVA_CPS_ORIGIN_ROOT')).canonicalFile
    def target = gradle.rootProject.allprojects.find { it.projectDir.canonicalFile == targetDir }
    if (target == null) return

    def fromSettings = []
    def mode = 'PREFER_PROJECT'
    if (settingsModel != null && GradleVersion.current() >= GradleVersion.version('6.8')) {
        def drm = settingsModel.dependencyResolutionManagement
        fromSettings = drm.repositories.toList()
        mode = drm.repositoriesMode.get().name()
    }
    def fromProject = target.repositories.toList()
    // Gradle's default PREFER_PROJECT ignores settings repositories when
    // the project declares any repository. We show both scopes nonetheless.
    def settingsEffective = mode != 'PREFER_PROJECT' || fromProject.isEmpty()
    def rows = []
    def other = false
    def appendRepos = { repos, scope, isEffective ->
        repos.each { r ->
            if (!(r instanceof MavenArtifactRepository)) {
                other = true
            } else {
                def u = r.url
                if (u != null && u.scheme in ['http','https'] && u.userInfo == null && u.query == null && u.fragment == null) {
                    rows.add([name:r.name, url:u.toString(), scope:scope, effective:isEffective])
                } else {
                    other = true
                }
            }
        }
    }
    // Keep active repositories first so existing C# de-duplication prefers
    // their scope when the same name+URL is declared in multiple places.
    if (settingsEffective) {
        appendRepos(fromSettings, 'settings', true)
        appendRepos(fromProject, 'project', false)
    } else {
        appendRepos(fromProject, 'project', true)
        appendRepos(fromSettings, 'settings', false)
    }
    // pluginManagement.repositories is only for Gradle plugins; it is NOT a
    // dependency repository source and must not be offered as such.
    def pluginOnlyCount = 0
    if (settingsModel != null) {
        pluginOnlyCount = settingsModel.pluginManagement.repositories.findAll {
            it instanceof MavenArtifactRepository && it.url?.host != 'plugins.gradle.org'
        }.size()
    }
    new File(System.getenv('JAVA_CPS_ORIGIN_OUTPUT')).setText(
        JsonOutput.toJson([repositories:rows, repositoriesMode:mode,
            otherRepositories:other, pluginOnlyCount:pluginOnlyCount]), 'UTF-8')
}
";
    }
}
