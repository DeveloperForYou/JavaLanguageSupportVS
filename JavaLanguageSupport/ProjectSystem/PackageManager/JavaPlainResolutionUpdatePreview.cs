using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;

namespace JavaLanguageSupport.ProjectSystem.PackageManager
{
    // JPM-S2D411-UPDATE-READONLY-20261125
    // Phase A of transactional updates. INSPECTION ONLY: never writes project,
    // installed JARs, transaction journal, or JDT LS workspace.
    // The S2D.3.5 crash recovery only understands additive journals; consequently
    // it would be unsafe to enable any update commit until a dedicated journal
    // and verifier for replaced references have been implemented and tested.
    internal static class JavaPlainResolutionUpdatePreview
    {
        internal sealed class Result
        {
            internal string Details;
            internal string PreviousJar;
            internal string ProposedJar;
            internal string PreviousSha256;
            internal string ProposedSha256;
            internal string Owner;
            internal string Scope;
        }

        private sealed class Existing
        {
            internal string Path;
            internal string Owner;
            internal string Version;
            internal string Scope;
        }

        internal static Result Inspect(string projectFile,
            JavaPlainMavenResolutionPlanner.Plan plan,
            JavaPlainResolutionTransactionPreflight.Validation prior,
            JavaPlainResolutionJarStaging.StageResult staged)
        {
            if (plan == null || prior == null || staged == null ||
                staged.NothingToStage || staged.Files == null ||
                plan.ClasspathDiffs == null)
                throw new InvalidOperationException(
                    "Eseguire Analizza con Maven, Valida transazione, Prepara JAR.");
            if (plan.HasOverride || plan.Unverified != 0 || plan.Added != 0 ||
                plan.Removed != 0 || plan.Updated != 1 ||
                plan.ClasspathDiffs.Count(x => x.Status == "Aggiornato") != 1)
                throw new NotSupportedException(
                    "S2D.4.1.1 supporta solo l'anteprima di UN aggiornamento " +
                    "transitivo, senza altre aggiunte/rimozioni, override o conflitti.");
            if (prior.BlockingIssueCount != 0 || prior.ChangedCount != 1 ||
                !string.Equals(prior.ManifestFingerprint, staged.SourceFingerprint,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Preflight e staging non coerenti: ripetere la preparazione.");
            string project = Path.GetFullPath(projectFile);
            if (!File.Exists(Io(project)) ||
                !string.Equals(Path.GetExtension(project), ".javaproj",
                    StringComparison.OrdinalIgnoreCase))
                throw new FileNotFoundException("Progetto Java assente.", project);
            string root = Path.GetDirectoryName(project);
            VerifySafe(root, project);
            string fingerprint = JavaPlainResolutionTransactionPreflight.CaptureFingerprint(
                project, CancellationToken.None);
            if (!string.Equals(fingerprint, staged.SourceFingerprint,
                StringComparison.Ordinal))
                throw new IOException("Progetto/JAR modificati dopo lo staging.");
            var installed = JavaPackageManagerInstalledReader.Read(project);
            if (!string.Equals(installed.Backend, "Plain", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Anteprima disponibile solo per progetti Plain.");

            var diff = plan.ClasspathDiffs.Single(x => x.Status == "Aggiornato");
            string[] coordinate = (diff.Coordinate ?? "").Split(':');
            if (coordinate.Length != 2 || coordinate.Any(x => !SafeToken(x)))
                throw new InvalidDataException("Coordinate Maven non sicure.");
            if (!SafeVersion(diff.InstalledVersion) || !SafeVersion(diff.ProposedVersion) ||
                string.Equals(diff.InstalledVersion, diff.ProposedVersion,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Versioni aggiornamento non valide.");
            // Disallow any non-identical scope / owner set. We want an actual
            // replacement of one owned transitive JAR, not a root version change.
            string plain, maven;
            ParseOwners(diff.RequiredBy, out plain, out maven);
            var rootOwners = installed.Items.Where(x => x.MavenCoordinate &&
                string.Equals(x.Coordinate + ":" + x.Version, maven,
                    StringComparison.OrdinalIgnoreCase)).ToList();
            if (rootOwners.Count != 1 ||
                !string.Equals(rootOwners[0].Coordinate, plain,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Proprietario Maven/Plain non univoco.");
            var owner = rootOwners[0];
            // The existing owner's version is intentionally NOT updated here:
            // we only model a transitive artifact version replacement.
            if (string.Equals(owner.Coordinate, diff.Coordinate,
                StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException(
                    "Aggiornamento della versione diretta: richiede una transazione dedicata.");
            string expectedOld = coordinate[0] + "." + coordinate[1] + "-" +
                diff.InstalledVersion + ".jar";
            string expectedNew = coordinate[0] + "." + coordinate[1] + "-" +
                diff.ProposedVersion + ".jar";
            var entries = ReadReferences(project, root);
            var old = entries.Where(x =>
                string.Equals(Path.GetFileName(x.Path), expectedOld,
                    StringComparison.OrdinalIgnoreCase)).ToList();
            if (old.Count != 1 ||
                !string.Equals(old[0].Owner, owner.Coordinate,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(old[0].Version, owner.Version, StringComparison.Ordinal) ||
                !string.Equals(JavaPackageScopes.Normalize(old[0].Scope),
                    JavaPackageScopes.Normalize(owner.Scope), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("JAR precedente non identificabile in modo univoco.");
            string ownerDir = Path.GetFullPath(Path.Combine(root, "lib", "java-packages",
                owner.GroupId, owner.ArtifactId, owner.Version));
            if (!string.Equals(Path.GetDirectoryName(old[0].Path), ownerDir,
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Il JAR precedente non risiede nella directory del proprietario.");
            string target = Path.GetFullPath(Path.Combine(ownerDir, expectedNew));
            VerifySafe(root, old[0].Path);
            VerifySafe(root, target);
            if (!File.Exists(Io(old[0].Path)))
                throw new FileNotFoundException("JAR precedente mancante.", old[0].Path);
            if (File.Exists(Io(target)) || Directory.Exists(Io(target)) ||
                entries.Any(x => string.Equals(x.Path, target,
                    StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Nuova destinazione gia occupata o referenziata: " + target);
            var file = staged.Files.SingleOrDefault(x =>
                string.Equals(x.Coordinate, diff.Coordinate, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Version, diff.ProposedVersion, StringComparison.OrdinalIgnoreCase));
            if (file == null || !string.Equals(file.FileName, expectedNew, StringComparison.Ordinal))
                throw new InvalidDataException("JAR proposto assente o rinominato nello staging.");
            string stageRoot = Path.GetFullPath(staged.StageDirectory);
            string stage = Path.GetFullPath(Path.Combine(stageRoot, "jars", file.FileName));
            string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\', '/');
            if (!string.Equals(Path.GetDirectoryName(stageRoot), tempRoot,
                StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(stageRoot).StartsWith("java-vsix-s2d32-", StringComparison.Ordinal) ||
                !Path.GetFileName(stageRoot).Substring("java-vsix-s2d32-".Length)
                    .All(Uri.IsHexDigit) ||
                Path.GetFileName(stageRoot).Length != "java-vsix-s2d32-".Length + 32)
                throw new InvalidDataException("Staging fuori dalla directory TEMP attesa.");
            VerifySafe(stageRoot, stage);
            if (!File.Exists(Io(stage)) ||
                new FileInfo(Io(stage)).Length != file.Size ||
                !string.Equals(Hash(stage), file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("SHA-256 JAR proposto non corrispondente allo staging.");
            string oldSha = Hash(old[0].Path);
            var recorded = prior.Manifest.SingleOrDefault(x =>
                string.Equals(x.RelativePath.Replace('/', '\\'),
                    old[0].Path.Substring(root.Length).TrimStart('\\', '/').Replace('/', '\\'),
                    StringComparison.OrdinalIgnoreCase));
            if (recorded == null || !string.Equals(recorded.Sha256, oldSha,
                StringComparison.OrdinalIgnoreCase))
                throw new IOException("JAR precedente diverso dal preflight.");
            string scope = JavaPackageScopes.Require(owner.Scope);
            var details = new StringBuilder();
            details.AppendLine("JPM-S2D.4.1.1 | ANTEPRIMA AGGIORNAMENTO (SOLO LETTURA)");
            details.AppendLine("Stato: VERIFICATO; COMMIT NON ABILITATO");
            details.AppendLine("Dipendenza: " + diff.Coordinate + ":" +
                diff.InstalledVersion + " -> " + diff.ProposedVersion);
            details.AppendLine("Proprietario: " + owner.Coordinate + ":" + owner.Version +
                " [" + scope + "]");
            details.AppendLine("Precedente: " + old[0].Path);
            details.AppendLine("  SHA-256 " + oldSha);
            details.AppendLine("Nuovo JAR proposto: " + target);
            details.AppendLine("  SHA-256 " + file.Sha256);
            details.AppendLine("Origine staging: " + stage);
            details.AppendLine("JAR manuali conservati nel piano: " + prior.ManualJarCount);
            details.AppendLine("La versione precedente resta intatta sul disco.");
            details.AppendLine("La proposta non cambia file, riferimenti, JDT LS o journal.");
            details.AppendLine("Prima del commit serve estendere journal/Crash Recovery a " +
                "sostituzioni di riferimenti, non solo aggiunte.");
            return new Result { Details = details.ToString().TrimEnd(),
                PreviousJar = old[0].Path, ProposedJar = target,
                PreviousSha256 = oldSha, ProposedSha256 = file.Sha256,
                Owner = owner.Coordinate + ":" + owner.Version, Scope = scope };
        }

        private static List<Existing> ReadReferences(string project, string root)
        {
            var options = new XmlReaderSettings { XmlResolver = null,
                DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 8 * 1024 * 1024 };
            var document = new XmlDocument { XmlResolver = null };
            using (var stream = new FileStream(Io(project), FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite))
            using (var reader = XmlReader.Create(stream, options)) document.Load(reader);
            if (document.DocumentElement == null || document.DocumentElement.LocalName != "Project")
                throw new InvalidDataException("Project XML non valido.");
            var results = new List<Existing>();
            foreach (XmlElement node in document.DocumentElement.SelectNodes(".//*").OfType<XmlElement>()
                .Where(x => x.LocalName == "JavaJarReference"))
            {
                string include = node.GetAttribute("Include");
                if (string.IsNullOrWhiteSpace(include) ||
                    include.IndexOfAny(new[] { '$', '@', '*', '?', '%', ';' }) >= 0 ||
                    Path.IsPathRooted(include))
                    throw new InvalidDataException("Include non letterale: " + include);
                string path = Path.GetFullPath(Path.Combine(root, include));
                VerifySafe(root, path);
                Func<string, string> read = key => node.ChildNodes.OfType<XmlElement>()
                    .Where(x => x.LocalName == key).Select(x => x.InnerText.Trim()).FirstOrDefault();
                results.Add(new Existing { Path = path,
                    Owner = read(JavaPlainPackageManager.ManagedId),
                    Version = read(JavaPlainPackageManager.ManagedVersion),
                    Scope = read(JavaPackageScopes.PlainMetadata) });
            }
            if (results.GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() != 1))
                throw new InvalidDataException("Riferimenti duplicati nel progetto.");
            return results;
        }

        private static void ParseOwners(string input, out string plain, out string maven)
        {
            const string prefix = "Plain: ";
            const string separator = " | Maven: ";
            if (input == null || !input.StartsWith(prefix, StringComparison.Ordinal) ||
                input.IndexOf(separator, StringComparison.Ordinal) <= prefix.Length)
                throw new InvalidDataException("Ownership non verificabile.");
            int offset = input.IndexOf(separator, StringComparison.Ordinal);
            plain = input.Substring(prefix.Length, offset - prefix.Length);
            maven = input.Substring(offset + separator.Length);
            if (plain.Contains(",") || maven.Contains(",") || plain == "—" || maven == "—")
                throw new InvalidDataException("Dipendenza condivisa/owner ambiguo.");
        }

        private static bool SafeToken(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.Length <= 160 &&
                value != "." && value != ".." &&
                value.All(x => char.IsLetterOrDigit(x) || x == '.' || x == '_' || x == '-');
        }

        private static bool SafeVersion(string value)
        {
            return SafeToken(value) && value.Length <= 120;
        }

        private static void VerifySafe(string root, string path)
        {
            string baseRoot = Path.GetFullPath(root).TrimEnd('\\', '/');
            string full = Path.GetFullPath(path);
            if (!string.Equals(full, baseRoot, StringComparison.OrdinalIgnoreCase) &&
                !full.StartsWith(baseRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Percorso fuori dalla directory prevista: " + full);
            if ((File.GetAttributes(Io(baseRoot)) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Root con junction/symlink non ammessa.");
            string current = baseRoot;
            foreach (string segment in full.Substring(baseRoot.Length).Split(
                new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if ((File.Exists(Io(current)) || Directory.Exists(Io(current))) &&
                    (File.GetAttributes(Io(current)) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Junction/symlink non ammessa: " + current);
            }
        }

        private static string Io(string path)
        {
            string full = Path.GetFullPath(path);
            return Path.DirectorySeparatorChar == '\\' &&
                !full.StartsWith(@"\\?\", StringComparison.Ordinal) ? @"\\?\" + full : full;
        }

        private static string Hash(string path)
        {
            using (var stream = new FileStream(Io(path), FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "")
                    .ToLowerInvariant();
        }
    }
}
