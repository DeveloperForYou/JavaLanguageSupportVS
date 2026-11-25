using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace JavaLanguageSupport.ProjectSystem.PackageManager
{
    // JPM-S2D31-TRANSACTION-PREFLIGHT-20261125
    // Phase 1 of transactional resolution: read-only snapshot and independently
    // repeated Maven simulation. NO JAR copying, project edit or commit permit.
    // Phase S2D.3.2/3.3 must still stage binaries, verify digests immediately
    // before commit, perform a recoverable swap and refresh JDT LS afterwards.
    internal static class JavaPlainResolutionTransactionPreflight
    {
        internal sealed class Validation
        {
            internal string Id;
            internal string ManifestFingerprint;
            internal int ReferencedJarCount;
            internal int ManualJarCount;
            internal int ChangedCount;
            internal bool ReadyForStaging;
            internal bool HasBlockers;
            // JPM-S2D32-ISOLATED-JAR-STAGING-20261125: manual jars are a commit-review warning, not a
            // blocker for copying new artifacts to a disposable TEMP directory.
            internal int BlockingIssueCount;
            internal IList<string> ManualPaths;
            internal string Details;
            internal IList<ManifestEntry> Manifest;
        }

        internal sealed class ManifestEntry
        {
            internal string RelativePath;
            internal long Length;
            internal string Sha256;
        }

        private sealed class Snapshot
        {
            internal string Fingerprint;
            internal int ReferencedJars;
            internal int ManualJars;
            internal long ProjectTicks;
            internal long ProjectLength;
            internal readonly List<ManifestEntry> Files = new List<ManifestEntry>();
            internal readonly List<string> ManualPaths = new List<string>();
        }

        internal static async Task<Validation> PrepareAsync(string projectFile,
            JavaPlainMavenResolutionPlanner.Plan original, CancellationToken token)
        {
            if (original == null || original.ClasspathDiffs == null)
                throw new InvalidOperationException("Prima eseguire Analizza con Maven.");
            if (string.IsNullOrWhiteSpace(projectFile))
                throw new InvalidOperationException("Selezionare un progetto Plain.");
            string full = Path.GetFullPath(projectFile);
            if (!full.EndsWith(".javaproj", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Il file selezionato non e un .javaproj.");
            token.ThrowIfCancellationRequested();
            Snapshot before = await Task.Run(() => Capture(full, token), token)
                .ConfigureAwait(false);
            if (before.ProjectTicks != original.ProjectStampTicks ||
                before.ProjectLength != original.ProjectFileLength)
                throw new InvalidOperationException(
                    "Il .javaproj e cambiato dalla simulazione: ripetere Analizza con Maven.");

            // Do not trust a plan generated from an older set of repositories or
            // dependency metadata. Re-resolve the exact same request from scratch.
            JavaPlainMavenResolutionPlanner.Plan repeated = await
                JavaPlainMavenResolutionPlanner.CreateAsync(full,
                    original.SelectedIdentity, original.TargetVersion, token)
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Snapshot after = await Task.Run(() => Capture(full, token), token)
                .ConfigureAwait(false);
            if (!string.Equals(before.Fingerprint, after.Fingerprint,
                    StringComparison.Ordinal) ||
                before.ProjectTicks != after.ProjectTicks ||
                before.ProjectLength != after.ProjectLength)
                throw new InvalidOperationException(
                    "Classpath Plain modificato durante il preflight: piano annullato.");
            VerifyEqualPlans(original, repeated);

            var audit = await Task.Run(() => JavaPlainVersionConflictGuard.Audit(full), token)
                .ConfigureAwait(false);
            // The audit itself might race with a dependency operation. Fail closed.
            Snapshot auditSnapshot = await Task.Run(() => Capture(full, token), token)
                .ConfigureAwait(false);
            if (!string.Equals(after.Fingerprint, auditSnapshot.Fingerprint,
                StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "JAR modificati durante l'audit: ripetere la validazione.");

            int changes = original.Added + original.Updated + original.Removed;
            var blockers = new List<string>();
            int criticalCount = 0;
            if (original.Unverified != 0 || audit.UnverifiedCount != 0)
            {
                blockers.Add("Sono presenti JAR gestiti non verificabili.");
                criticalCount++;
            }
            if (audit.ConflictCount != 0)
            {
                blockers.Add("Il classpath Plain contiene gia conflitti di versione.");
                criticalCount++;
            }
            if (after.ManualJars != 0)
                blockers.Add("Sono presenti JAR esterni/manuali: preservarne esplicitamente i riferimenti.");
            bool noChanges = changes == 0;
            if (original.Removed != 0)
            {
                blockers.Add("Il piano prevede rimozioni: verificare i consumatori prima di autorizzarle.");
                criticalCount++;
            }

            var text = new StringBuilder();
            string id = Guid.NewGuid().ToString("N");
            text.AppendLine("JPM-S2D.3.1 | PREFLIGHT TRANSAZIONALE (SOLO LETTURA)");
            text.AppendLine("ID verifica: " + id);
            text.AppendLine("Progetto: " + Path.GetFileName(full));
            text.AppendLine("Manifest SHA-256: " + after.Fingerprint);
            text.AppendLine("JAR referenziati controllati con SHA-256: " + after.ReferencedJars);
            text.AppendLine("JAR manuali / esterni: " + after.ManualJars);
            text.AppendLine("Manifest binari correnti (NON ancora backup):");
            foreach (ManifestEntry entry in after.Files)
                text.AppendLine("  " + entry.RelativePath + " | " + entry.Length +
                    " bytes | SHA-256 " + entry.Sha256);
            text.AppendLine("Nuova risoluzione Maven: corrispondente al piano S2D.2.");
            text.AppendLine("Differenze: +" + original.Added + " ~" + original.Updated +
                " -" + original.Removed + " =" + original.Unchanged + ".");
            text.AppendLine("Stato: " + (blockers.Count != 0 ?
                "DA RIVEDERE: nessuna transazione autorizzata." : noChanges ?
                "OK: nessuna differenza da preparare; classpath invariato." :
                "PREFLIGHT SUPERATO: pronto per il futuro staging, NON per il commit."));
            foreach (string blocker in blockers)
                text.AppendLine("  BLOCCO / REVISIONE: " + blocker);
            text.AppendLine("NOTE: i JAR proposti non sono stati scaricati, verificati o copiati.");
            text.AppendLine("NOTE: la compatibilita binaria/API e il runtime classpath non sono garantiti.");
            text.AppendLine("NOTE: un futuro commit dovra ricontrollare SHA-256 e progetto immediatamente prima della scrittura.");
            text.AppendLine("Nessun file di progetto, JAR o workspace JDT LS modificato.");
            return new Validation {
                Id = id, ManifestFingerprint = after.Fingerprint,
                ReferencedJarCount = after.ReferencedJars, ManualJarCount = after.ManualJars,
                ChangedCount = changes, ReadyForStaging = blockers.Count == 0 && !noChanges,
                HasBlockers = blockers.Count != 0,
                BlockingIssueCount = criticalCount,
                ManualPaths = after.ManualPaths.AsReadOnly(),
                Manifest = after.Files.AsReadOnly(),
                Details = text.ToString().TrimEnd()
            };
        }

        private static void VerifyEqualPlans(JavaPlainMavenResolutionPlanner.Plan oldPlan,
            JavaPlainMavenResolutionPlanner.Plan newPlan)
        {
            if (oldPlan.HasOverride != newPlan.HasOverride ||
                !string.Equals(oldPlan.SelectedIdentity, newPlan.SelectedIdentity,
                    StringComparison.Ordinal) ||
                !string.Equals(oldPlan.TargetVersion, newPlan.TargetVersion,
                    StringComparison.Ordinal) ||
                oldPlan.DirectCount != newPlan.DirectCount ||
                oldPlan.Unverified != newPlan.Unverified ||
                oldPlan.Added != newPlan.Added || oldPlan.Updated != newPlan.Updated ||
                oldPlan.Removed != newPlan.Removed || oldPlan.Unchanged != newPlan.Unchanged ||
                oldPlan.CurrentJarCount != newPlan.CurrentJarCount ||
                oldPlan.SimulatedJarCount != newPlan.SimulatedJarCount ||
                !string.Equals(oldPlan.TargetGroup, newPlan.TargetGroup,
                    StringComparison.Ordinal) ||
                !string.Equals(oldPlan.TargetArtifact, newPlan.TargetArtifact,
                    StringComparison.Ordinal) ||
                !string.Equals(PlanRows(oldPlan), PlanRows(newPlan), StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "La nuova risoluzione Maven non corrisponde al piano: rieseguire l'analisi.");
        }

        private static string PlanRows(JavaPlainMavenResolutionPlanner.Plan plan)
        {
            if (plan.ClasspathDiffs == null)
                throw new InvalidDataException("Piano Maven privo del confronto classpath.");
            var lines = plan.ClasspathDiffs.Select(x => string.Join("\t", new[] {
                x.Status ?? "", x.Coordinate ?? "", x.InstalledVersion ?? "",
                x.ProposedVersion ?? "", x.Scope ?? "", x.RequiredBy ?? "" }));
            return string.Join("\n", lines.OrderBy(x => x, StringComparer.Ordinal));
        }

        // JPM-S2D32-ISOLATED-JAR-STAGING-20261125: independent final check after copy-dependencies.
        internal static string CaptureFingerprint(string projectFile, CancellationToken token)
        {
            return Capture(Path.GetFullPath(projectFile), token).Fingerprint;
        }

        private static Snapshot Capture(string projectFile, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var projectInfo = new FileInfo(projectFile);
            if (!projectInfo.Exists || projectInfo.Length > 8 * 1024 * 1024)
                throw new InvalidDataException("File .javaproj assente o oltre il limite XML (8 MB).");
            string root = Path.GetDirectoryName(projectFile);
            EnsureNoReparse(root, projectFile);
            long stamp = projectInfo.LastWriteTimeUtc.Ticks;
            long size = projectInfo.Length;
            string projectSha = HashFile(projectFile, token);
            projectInfo.Refresh();
            if (!projectInfo.Exists || projectInfo.LastWriteTimeUtc.Ticks != stamp ||
                projectInfo.Length != size)
                throw new IOException("Il .javaproj e cambiato durante la lettura.");

            var settings = new XmlReaderSettings { XmlResolver = null,
                DtdProcessing = DtdProcessing.Prohibit,
                MaxCharactersInDocument = 8 * 1024 * 1024 };
            var xml = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(projectFile, settings)) xml.Load(reader);
            if (xml.DocumentElement == null || xml.DocumentElement.LocalName != "Project")
                throw new InvalidDataException("Il file .javaproj non contiene Project.");
            var unique = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var manualPaths = new List<string>();
            int manual = 0;
            string managedRoot = Path.Combine(root, "lib", "java-packages")
                .TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            foreach (XmlNode group in xml.DocumentElement.ChildNodes)
            {
                var itemGroup = group as XmlElement;
                if (itemGroup == null || itemGroup.LocalName != "ItemGroup") continue;
                foreach (XmlNode child in itemGroup.ChildNodes)
                {
                    var item = child as XmlElement;
                    if (item == null || item.LocalName != "JavaJarReference") continue;
                    token.ThrowIfCancellationRequested();
                    string include = item.GetAttribute("Include");
                    if (string.IsNullOrWhiteSpace(include) ||
                        include.IndexOfAny(new[] { '*', '?', '$', '%', ';', '\r', '\n' }) >= 0 ||
                        !include.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("JavaJarReference con Include non verificabile.");
                    string path = Path.GetFullPath(Path.IsPathRooted(include) ? include :
                        Path.Combine(root, include));
                    if (!IsBelow(root, path))
                        throw new InvalidDataException("JAR esterno alla directory del progetto: " +
                            Path.GetFileName(path));
                    EnsureNoReparse(root, path);
                    if (!File.Exists(path))
                        throw new FileNotFoundException("JAR referenziato non presente", path);
                    var owner = item.ChildNodes.OfType<XmlElement>().FirstOrDefault(x =>
                        x.LocalName == JavaPlainPackageManager.ManagedId);
                    if (owner == null || string.IsNullOrWhiteSpace(owner.InnerText) ||
                        !path.StartsWith(managedRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        manual++;
                        manualPaths.Add(path.Substring(root.Length).TrimStart('\\', '/'));
                    }
                    if (!unique.ContainsKey(path)) unique.Add(path, null);
                }
            }
            var fingerprint = new StringBuilder().Append(projectSha).Append('\n');
            var manifestEntries = new List<ManifestEntry>();
            foreach (string path in unique.Keys)
            {
                token.ThrowIfCancellationRequested();
                var jarInfo = new FileInfo(path);
                long jarStamp = jarInfo.LastWriteTimeUtc.Ticks;
                long jarSize = jarInfo.Length;
                string sha = HashFile(path, token);
                jarInfo.Refresh();
                if (!jarInfo.Exists || jarInfo.LastWriteTimeUtc.Ticks != jarStamp ||
                    jarInfo.Length != jarSize)
                    throw new IOException("JAR cambiato durante il preflight: " + Path.GetFileName(path));
                string relative = path.Substring(root.Length).TrimStart('\\', '/');
                manifestEntries.Add(new ManifestEntry {
                    RelativePath = relative, Length = jarSize, Sha256 = sha });
                fingerprint.Append(relative).Append('|')
                    .Append(jarSize).Append('|').Append(sha).Append('\n');
            }
            projectInfo.Refresh();
            if (!projectInfo.Exists || projectInfo.LastWriteTimeUtc.Ticks != stamp ||
                projectInfo.Length != size ||
                !string.Equals(projectSha, HashFile(projectFile, token), StringComparison.Ordinal))
                throw new IOException("Il .javaproj e cambiato durante il preflight.");
            byte[] digest;
            using (var sha = SHA256.Create())
                digest = sha.ComputeHash(Encoding.UTF8.GetBytes(fingerprint.ToString()));
            var snapshot = new Snapshot {
                Fingerprint = Hex(digest), ReferencedJars = unique.Count,
                ManualJars = manual, ProjectTicks = stamp, ProjectLength = size
            };
            snapshot.Files.AddRange(manifestEntries);
            snapshot.ManualPaths.AddRange(manualPaths);
            return snapshot;
        }

        private static bool IsBelow(string root, string path)
        {
            return path.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsureNoReparse(string root, string path)
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Root del progetto con reparse point non supportata.");
            string current = path;
            while (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
            {
                if (!IsBelow(root, current))
                    throw new InvalidDataException("Percorso fuori dal progetto.");
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Link simbolico/Junction non supportato: " +
                        Path.GetFileName(current));
                current = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(current))
                    throw new InvalidDataException("Percorso non valido.");
            }
        }

        private static string HashFile(string path, CancellationToken token)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[128 * 1024];
                int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    token.ThrowIfCancellationRequested();
                    sha.TransformBlock(buffer, 0, count, buffer, 0);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return Hex(sha.Hash);
            }
        }

        private static string Hex(byte[] data)
        {
            return BitConverter.ToString(data).Replace("-", "").ToLowerInvariant();
        }
    }
}
