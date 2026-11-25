using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace JavaLanguageSupport.ProjectSystem.PackageManager
{
    // JPM-S2D32-ISOLATED-JAR-STAGING-20261125
    // Copies the Maven-mediated proposal into an isolated, disposable TEMP directory.
    // No write to the project, .javaproj, resolved Plain JAR directories or JDT LS.
    // Staging is NOT a commit permit; all source hashes require revalidation at commit.
    internal static class JavaPlainResolutionJarStaging
    {
        internal sealed class StageResult
        {
            internal string StageDirectory;
            internal string ValidationId;
            internal string SourceFingerprint;
            internal bool NothingToStage;
            internal int StagedJarCount;
            internal int ManualJarCount;
            internal IList<StagedJar> Files;
            internal string Details;
        }

        internal sealed class StagedJar
        {
            internal string Coordinate;
            internal string Version;
            internal string FileName;
            internal long Size;
            internal string Sha256;
        }

        private const string StagePrefix = "java-vsix-s2d32-";
        // Maven plugin and naming options match the established Plain resolver.
        private const string MavenCopyRunner = @"
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
    throw 'Maven non trovato: impostare PATH/MAVEN_HOME o mvnw.cmd nel progetto.'
}
try {
    $launcher = Find-Maven
    & $launcher '-B' '-ntp' '-f' $env:JAVA_CPS_PLAIN_POM '-N' 'org.apache.maven.plugins:maven-dependency-plugin:3.7.1:copy-dependencies' '-DincludeScope=test' '-DexcludeTypes=pom' ('-DoutputDirectory=' + $env:JAVA_CPS_PLAIN_OUT) '-Dmdep.useRepositoryLayout=false' '-Dmdep.prependGroupId=true'
    if ($LASTEXITCODE -ne 0) { throw ('Maven exit code: ' + $LASTEXITCODE) }
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
";

        internal static async Task<StageResult> PrepareAsync(string projectFile,
            JavaPlainMavenResolutionPlanner.Plan original,
            JavaPlainResolutionTransactionPreflight.Validation prior,
            CancellationToken token)
        {
            if (prior == null || original == null || original.ClasspathDiffs == null)
                throw new InvalidOperationException(
                    "Prima eseguire Analizza con Maven e Valida transazione.");
            string full = Path.GetFullPath(projectFile);
            token.ThrowIfCancellationRequested();
            // NEVER use a stale validation. Re-run the entire S2D3.1 preflight.
            var current = await JavaPlainResolutionTransactionPreflight.PrepareAsync(
                full, original, token).ConfigureAwait(false);
            if (!string.Equals(prior.ManifestFingerprint, current.ManifestFingerprint,
                StringComparison.Ordinal) || prior.ChangedCount != current.ChangedCount)
                throw new InvalidOperationException(
                    "Manifest del progetto diverso dall'ultima validazione: ripetere Valida transazione.");
            if (current.BlockingIssueCount != 0)
                throw new InvalidOperationException(
                    "Il piano contiene blocchi non risolti: nessuno staging consentito. " +
                    "Consultare il preflight JPM-S2D.3.1.");
            // Manual JARs are not blockers for PREPARATION. Preserve their manifest
            // references/digests. A future commit must preserve them explicitly.
            if (current.ChangedCount == 0)
                return new StageResult {
                    ValidationId = current.Id,
                    SourceFingerprint = current.ManifestFingerprint,
                    NothingToStage = true,
                    StagedJarCount = 0,
                    ManualJarCount = current.ManualJarCount,
                    Files = new List<StagedJar>().AsReadOnly(),
                    Details = "JPM-S2D.3.2 | STAGING ISOLATO - NESSUNA DIFFERENZA\n" +
                        "Classpath Maven e Plain allineati: nessun JAR da preparare.\n" +
                        "JAR manuali invariati: " + current.ManualJarCount + ".\n" +
                        "Manifest sorgente SHA-256: " + current.ManifestFingerprint + "\n" +
                        "Nessun file del progetto o workspace JDT LS modificato."
                };

            // Build a strict expected artifact set from the resolved Maven graph.
            // If Maven copies additional artifacts (e.g. classifiers, other types),
            // refuse the stage rather than guessing about the target classpath.
            var expected = BuildExpected(original);
            if (expected.Count != original.SimulatedJarCount)
                throw new InvalidDataException(
                    "Numero di JAR proposti non corrispondente al grafo Maven.");
            token.ThrowIfCancellationRequested();
            string stage = Path.Combine(Path.GetTempPath(), StagePrefix +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            bool completed = false;
            try
            {
                string jarsDir = Path.Combine(stage, "jars");
                Directory.CreateDirectory(jarsDir);
                string pom = Path.Combine(stage, "resolution-pom.xml");
                JavaPlainMavenResolutionPlanner.WriteStagingPom(full, original, pom, token);
                string script = Path.Combine(stage, "copy.ps1");
                File.WriteAllText(script, MavenCopyRunner, new UTF8Encoding(false));
                JavaJpmProjectLogger.Info(full, "JPM S2D.3.2",
                    "Avvio staging Maven isolato, JAR previsti: " + expected.Count +
                    "; JAR manuali preservati: " + current.ManualJarCount + ".");
                await ExecuteCopyAsync(full, script, pom, jarsDir, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                List<StagedJar> staged = VerifyOutput(jarsDir, expected, token);
                // Independently hash ALL original references after Maven has run.
                // Maven's cache may change; the Plain project must not.
                string endingFingerprint = await Task.Run(() =>
                    JavaPlainResolutionTransactionPreflight.CaptureFingerprint(full, token),
                    token).ConfigureAwait(false);
                if (!string.Equals(current.ManifestFingerprint, endingFingerprint,
                    StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Classpath Plain modificato durante lo staging: preparazione scartata.");

                var manifest = new StringBuilder();
                manifest.AppendLine("JPM-S2D.3.2 | MANIFEST STAGING (NON AUTORIZZA COMMIT)");
                manifest.AppendLine("Progetto: " + Path.GetFileName(full));
                manifest.AppendLine("ID validazione: " + current.Id);
                manifest.AppendLine("Manifest sorgente SHA-256: " + current.ManifestFingerprint);
                manifest.AppendLine("Staging: " + stage);
                manifest.AppendLine("JAR preparati: " + staged.Count);
                manifest.AppendLine("JAR MANUALI DA PRESERVARE: " + current.ManualJarCount);
                foreach (var jar in staged)
                    manifest.AppendLine("  " + jar.Coordinate + ":" + jar.Version +
                        " | " + jar.FileName + " | " + jar.Size + " bytes | SHA-256 " + jar.Sha256);
                foreach (string manual in current.ManualPaths ?? new List<string>())
                {
                    var item = current.Manifest.FirstOrDefault(x => string.Equals(
                        x.RelativePath, manual, StringComparison.OrdinalIgnoreCase));
                    if (item == null) throw new InvalidDataException(
                        "Riferimento JAR manuale non incluso nel manifest: " + manual);
                    manifest.AppendLine("  PRESERVARE " + manual + " | " + item.Length +
                        " bytes | SHA-256 " + item.Sha256);
                }
                manifest.AppendLine("ATTENZIONE: non e stato creato alcun backup/rollback.");
                manifest.AppendLine("Nessuna garanzia di compatibilita binaria/API.");
                manifest.AppendLine("Prima del commit servono conferma, backup e nuova verifica SHA-256.");
                manifest.AppendLine("Nessun .javaproj, JAR installato, classpath o JDT LS modificato.");
                File.WriteAllText(Path.Combine(stage, "staging-manifest.txt"),
                    manifest.ToString(), new UTF8Encoding(false));
                token.ThrowIfCancellationRequested();
                completed = true;
                return new StageResult {
                    StageDirectory = stage,
                    ValidationId = current.Id,
                    SourceFingerprint = current.ManifestFingerprint,
                    ManualJarCount = current.ManualJarCount,
                    StagedJarCount = staged.Count,
                    Files = staged.AsReadOnly(),
                    Details = manifest.ToString().TrimEnd()
                };
            }
            finally
            {
                if (!completed) Discard(stage);
            }
        }

        private static Dictionary<string, StagedJar> BuildExpected(
            JavaPlainMavenResolutionPlanner.Plan plan)
        {
            var expected = new Dictionary<string, StagedJar>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in plan.ClasspathDiffs)
            {
                if (row.Status == "Rimosso") continue;
                if (row.Status != "Invariato" && row.Status != "Aggiunto" &&
                    row.Status != "Aggiornato")
                    throw new InvalidDataException("Stato diff non supportato per lo staging.");
                string[] parts = (row.Coordinate ?? "").Split(':');
                if (parts.Length != 2) throw new InvalidDataException(
                    "Coordinate non Maven nel piano: " + row.Coordinate);
                RequireArtifactToken(parts[0], 160);
                RequireArtifactToken(parts[1], 160);
                string version = row.ProposedVersion;
                RequireVersion(version);
                string fileName = parts[0] + "." + parts[1] + "-" + version + ".jar";
                if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    fileName.Length > 230 || fileName == "." || fileName == "..")
                    throw new InvalidDataException("Nome JAR non sicuro: " + fileName);
                if (expected.ContainsKey(fileName))
                    throw new InvalidDataException("Due coordinate Maven mappano lo stesso JAR: " + fileName);
                expected.Add(fileName, new StagedJar {
                    Coordinate = row.Coordinate, Version = version, FileName = fileName });
            }
            if (expected.Count == 0) throw new InvalidDataException("Classpath Maven proposto vuoto.");
            return expected;
        }

        private static void RequireArtifactToken(string text, int max)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > max ||
                !text.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_'))
                throw new InvalidDataException("Coordinate Maven non supportate nello staging.");
        }

        private static void RequireVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version) || version.Length > 120 ||
                !version.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' ||
                    c == '_' || c == '+'))
                throw new InvalidDataException("Versione Maven non valida nello staging.");
        }

        private static List<StagedJar> VerifyOutput(string directory,
            IDictionary<string, StagedJar> expected, CancellationToken token)
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Directory di staging non sicura (reparse point).");
            string[] files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
            if (files.Length != expected.Count)
                throw new InvalidDataException("JAR copiati da Maven: " + files.Length +
                    ", attesi: " + expected.Count + ". Nessun piano applicabile.");
            var result = new List<StagedJar>();
            foreach (string path in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                if (!string.Equals(Path.GetDirectoryName(path), directory,
                    StringComparison.OrdinalIgnoreCase) ||
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Output Maven non sicuro o annidato.");
                string fileName = Path.GetFileName(path);
                StagedJar jar;
                if (!expected.TryGetValue(fileName, out jar))
                    throw new InvalidDataException("Maven ha copiato un JAR inatteso: " + fileName);
                var file = new FileInfo(path);
                if (!file.Exists || file.Length == 0)
                    throw new InvalidDataException("JAR staged vuoto o assente: " + fileName);
                long length = file.Length;
                long ticks = file.LastWriteTimeUtc.Ticks;
                string digest = Hash(path, token);
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    if (zip.Entries.Count == 0)
                        throw new InvalidDataException("Archivio JAR senza elementi: " + fileName);
                }
                file.Refresh();
                if (!file.Exists || file.Length != length ||
                    file.LastWriteTimeUtc.Ticks != ticks)
                    throw new IOException("JAR staged modificato durante la verifica: " + fileName);
                jar.Size = length;
                jar.Sha256 = digest;
                result.Add(jar);
            }
            return result;
        }

        private static string Hash(string path, CancellationToken token)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                var buffer = new byte[65536];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    token.ThrowIfCancellationRequested();
                    sha.TransformBlock(buffer, 0, read, buffer, 0);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static async Task ExecuteCopyAsync(string projectFile, string script,
            string pom, string target, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string root = Path.GetDirectoryName(projectFile);
            var start = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\"")
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.EnvironmentVariables["JAVA_CPS_PLAIN_ROOT"] = root;
            start.EnvironmentVariables["JAVA_CPS_PLAIN_POM"] = pom;
            start.EnvironmentVariables["JAVA_CPS_PLAIN_OUT"] = target;
            using (var process = new Process { StartInfo = start })
            {
                if (!process.Start()) throw new IOException("Impossibile avviare PowerShell per lo staging.");
                using (token.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(); }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }))
                {
                    Task<string> output = process.StandardOutput.ReadToEndAsync();
                    Task<string> error = process.StandardError.ReadToEndAsync();
                    await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
                    string stdout = await output.ConfigureAwait(false);
                    string stderr = await error.ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0)
                        throw new IOException("Maven copy-dependencies fallito (exit " +
                            process.ExitCode + "): " + Tail(stderr + "\n" + stdout));
                    JavaJpmProjectLogger.Info(projectFile, "JPM S2D.3.2",
                        "Maven copy-dependencies completato (exit 0). " + Tail(stdout));
                }
            }
        }

        private static string Tail(string value)
        {
            value = (value ?? "").Trim();
            return value.Length <= 900 ? value : value.Substring(value.Length - 900);
        }

        // Only delete disposable directories created by THIS phase under TEMP.
        // No arbitrary delete paths accepted, no project files or external JARs.
        internal static void Discard(string stage)
        {
            if (string.IsNullOrWhiteSpace(stage)) return;
            string full = Path.GetFullPath(stage).TrimEnd('\\', '/');
            string expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\', '/');
            string name = Path.GetFileName(full);
            if (!string.Equals(Path.GetDirectoryName(full), expectedParent,
                StringComparison.OrdinalIgnoreCase) ||
                !name.StartsWith(StagePrefix, StringComparison.Ordinal) ||
                name.Length != StagePrefix.Length + 32 ||
                !name.Skip(StagePrefix.Length).All(Uri.IsHexDigit)) return;
            if (!Directory.Exists(full)) return;
            try
            {
                // Refuse to traverse directory junctions or symlinks.
                foreach (string nested in Directory.GetDirectories(full, "*", SearchOption.AllDirectories))
                    if ((File.GetAttributes(nested) & FileAttributes.ReparsePoint) != 0)
                        return;
                if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return;
                Directory.Delete(full, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
