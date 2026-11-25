using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using JavaLanguageSupport.ProjectSystem.Dependencies;

namespace JavaLanguageSupport.ProjectSystem.PackageManager
{
    // JPM-S2D33-ADDITIVE-TRANSACTION-20261125
    // Strict first commit capability: ONLY added Maven jars with one unambiguous
    // direct Plain owner. Updates/removals/shared roots are deliberately rejected.
    // All existing binaries and manual references are preserved byte for byte.
    internal static class JavaPlainResolutionCommit
    {
        internal sealed class CommitResult
        {
            internal string TransactionDirectory;
            internal int Added;
            internal string Details;
        }

        private sealed class Addition
        {
            internal string Coordinate;
            internal string Version;
            internal string Owner;
            internal string OwnerVersion;
            internal string Scope;
            internal string Source;
            internal string Target;
            internal string Sha256;
            internal long Size;
        }

        private const string JournalName = "plain-resolution-transactions";
        private const string VersionTag = "JPM-S2D.3.3";
        private static readonly SemaphoreSlim CommitGate = new SemaphoreSlim(1, 1);

        // For the confirmation window, build an exact, non-mutating proposed plan.
        internal static string Preview(string projectFile,
            JavaPlainMavenResolutionPlanner.Plan plan,
            JavaPlainResolutionJarStaging.StageResult staged)
        {
            var changes = ValidateAdditiveRequest(projectFile, plan, staged);
            var text = new StringBuilder();
            text.AppendLine(VersionTag + " | RIEPILOGO PRIMA DEL COMMIT");
            text.AppendLine("Progetto: " + Path.GetFullPath(projectFile));
            text.AppendLine("JAR da aggiungere: " + changes.Count);
            foreach (var item in changes)
                text.AppendLine("+ " + item.Coordinate + ":" + item.Version +
                    " -> " + item.Owner + ":" + item.OwnerVersion +
                    " [" + item.Scope + "]");
            text.AppendLine("JAR manuali da mantenere: " + staged.ManualJarCount);
            text.AppendLine("Nessun JAR esistente verra modificato o rimosso.");
            text.AppendLine("Creazione backup .javaproj e registrazione transazione.");
            text.AppendLine("Dopo il commit: aggiornamento del modello JDT LS.");
            text.AppendLine("La compatibilita API/binaria NON e garantita.");
            return text.ToString().TrimEnd();
        }

        internal static async Task<CommitResult> ApplyAsync(string projectFile,
            JavaPlainMavenResolutionPlanner.Plan plan,
            JavaPlainResolutionTransactionPreflight.Validation prior,
            JavaPlainResolutionJarStaging.StageResult staged)
        {
            // No UI CancellationToken: after user confirmation the transaction must
            // either finish or execute its rollback. No interleaving with another commit.
            await CommitGate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await Task.Run(() => ApplyCore(projectFile, plan, prior, staged))
                    .ConfigureAwait(false);
            }
            finally { CommitGate.Release(); }
        }

        private static CommitResult ApplyCore(string projectFile,
            JavaPlainMavenResolutionPlanner.Plan plan,
            JavaPlainResolutionTransactionPreflight.Validation prior,
            JavaPlainResolutionJarStaging.StageResult staged)
        {
            string project = Path.GetFullPath(projectFile);
            // Cross-process lock: two Visual Studio instances must not perform
            // S2D.3.3 commits on the same Plain project concurrently.
            string key;
            using (var digest = SHA256.Create())
                key = BitConverter.ToString(digest.ComputeHash(
                    Encoding.UTF8.GetBytes(project.ToUpperInvariant()))).Replace("-", "");
            using (var mutex = new Mutex(false, @"Local\JavaLanguageSupportVS-JPM-S2D33-" + key))
            {
                bool entered;
                try { entered = mutex.WaitOne(TimeSpan.Zero); }
                catch (AbandonedMutexException) { entered = true; }
                if (!entered)
                    throw new InvalidOperationException("Un'altra istanza VS sta gia applicando un piano a questo progetto.");
                try { return ApplyCoreLocked(project, plan, prior, staged); }
                finally { mutex.ReleaseMutex(); }
            }
        }

        private static CommitResult ApplyCoreLocked(string project,
            JavaPlainMavenResolutionPlanner.Plan plan,
            JavaPlainResolutionTransactionPreflight.Validation prior,
            JavaPlainResolutionJarStaging.StageResult staged)
        {
            if (prior == null || string.IsNullOrWhiteSpace(prior.ManifestFingerprint) ||
                staged == null || staged.NothingToStage ||
                !string.Equals(prior.ManifestFingerprint, staged.SourceFingerprint,
                    StringComparison.Ordinal))
                throw new InvalidOperationException("Manifest preflight/staging non corrispondente: ripetere la preparazione.");
            List<Addition> additions = ValidateAdditiveRequest(project, plan, staged);

            // Re-resolve Maven and re-hash EVERY source reference immediately before
            // preparing writes. This also checks the chosen plan and repository data.
            var validated = JavaPlainResolutionTransactionPreflight.PrepareAsync(
                project, plan, CancellationToken.None).GetAwaiter().GetResult();
            if (validated.BlockingIssueCount != 0 || validated.ChangedCount != additions.Count ||
                !string.Equals(validated.ManifestFingerprint, staged.SourceFingerprint,
                    StringComparison.Ordinal))
                throw new InvalidOperationException("Validazione finale differente: progetto o dipendenze cambiati.");

            VerifyStage(staged, plan);
            ValidateTargets(project, additions);
            var xml = ReadXml(project);
            AddReferences(xml, additions);
            // Serialize BEFORE we touch any referenced JAR or the project file.
            string projectRoot = Path.GetDirectoryName(project);
            string txRoot = Path.Combine(projectRoot, ".java-vs", JournalName);
            EnsureTreeSafe(projectRoot, Path.GetDirectoryName(txRoot));
            Directory.CreateDirectory(txRoot);
            EnsureTreeSafe(projectRoot, txRoot);
            RefuseInterruptedTransactions(txRoot);
            string transaction = Path.Combine(txRoot, "tx-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(transaction);
            string backup = Path.Combine(transaction, "project-original.javaproj");
            string proposed = Path.Combine(transaction, "project-proposed.javaproj");
            string journal = Path.Combine(transaction, "transaction-state.txt");
            string projectSha = Sha(project);
            File.Copy(project, backup, false);
            if (!string.Equals(Sha(backup), projectSha, StringComparison.Ordinal))
                throw new IOException("Backup progetto non verificato: nessuna modifica eseguita.");
            WriteXml(xml, proposed, FileHasBom(project));
            var addedTargets = new List<string>();
            bool projectReplaced = false;
            try
            {
                WriteJournal(journal, "PREPARED", project, projectSha, additions);
                // Stage the entire set of new files in their final directory first.
                // Never overwrite any existing, even unreferenced, JAR.
                foreach (var add in additions)
                {
                    // Recheck only THIS destination: earlier additions are already
                    // present by design and must not invalidate later additions.
                    EnsureTreeSafe(projectRoot, Path.GetDirectoryName(add.Target));
                    if (File.Exists(add.Target) || Directory.Exists(add.Target))
                        throw new IOException("Destinazione JAR gia occupata: " + add.Target);
                    string directory = Path.GetDirectoryName(add.Target);
                    Directory.CreateDirectory(directory);
                    EnsureTreeSafe(projectRoot, directory);
                    string pending = add.Target + ".jpm-" + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        using (var input = new FileStream(add.Source, FileMode.Open, FileAccess.Read, FileShare.Read))
                        using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            input.CopyTo(output);
                            output.Flush(true);
                        }
                        if (new FileInfo(pending).Length != add.Size ||
                            !string.Equals(Sha(pending), add.Sha256, StringComparison.Ordinal))
                            throw new IOException("SHA-256 diverso dopo la copia: " + add.Coordinate);
                        if (File.Exists(add.Target))
                            throw new IOException("Destinazione JAR creata da un altro processo: " + add.Target);
                        File.Move(pending, add.Target);
                        addedTargets.Add(add.Target);
                    }
                    finally { if (File.Exists(pending)) File.Delete(pending); }
                }
                WriteJournal(journal, "JARS_COPIED", project, projectSha, additions);
                // An external package operation may have changed the project while
                // we copied the JARs. Abort instead of overwriting their edits.
                string now = JavaPlainResolutionTransactionPreflight.CaptureFingerprint(
                    project, CancellationToken.None);
                if (!string.Equals(now, staged.SourceFingerprint, StringComparison.Ordinal) ||
                    !string.Equals(Sha(project), projectSha, StringComparison.Ordinal))
                    throw new IOException("Il .javaproj o i JAR sono cambiati durante il commit.");
                foreach (var add in additions)
                    if (new FileInfo(add.Target).Length != add.Size ||
                        !string.Equals(Sha(add.Target), add.Sha256, StringComparison.Ordinal))
                        throw new IOException("JAR destinazione modificato prima del commit: " + add.Target);
                // Atomic NTFS replacement; keep both durable preimage and proposed
                // image under .java-vs for manual recovery after a process crash.
                string replaceFile = Path.Combine(projectRoot,
                    ".jpm-commit-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    File.Copy(proposed, replaceFile, false);
                    File.Replace(replaceFile, project, null, true);
                    projectReplaced = true;
                }
                finally { if (File.Exists(replaceFile)) File.Delete(replaceFile); }
                WriteJournal(journal, "PROJECT_COMMITTED", project, projectSha, additions);
            }
            catch (Exception cause)
            {
                Exception rollbackFailure = null;
                if (projectReplaced)
                {
                    try
                    {
                        string restore = Path.Combine(projectRoot,
                            ".jpm-rollback-" + Guid.NewGuid().ToString("N") + ".tmp");
                        try
                        {
                            File.Copy(backup, restore, false);
                            File.Replace(restore, project, null, true);
                        }
                        finally { if (File.Exists(restore)) File.Delete(restore); }
                        if (!string.Equals(Sha(project), projectSha, StringComparison.Ordinal))
                            throw new IOException("SHA-256 progetto diverso dopo il rollback.");
                    }
                    catch (Exception ex) { rollbackFailure = ex; }
                }
                if (rollbackFailure == null)
                {
                    foreach (var path in addedTargets)
                    {
                        try { if (File.Exists(path)) File.Delete(path); }
                        catch (Exception ex) { rollbackFailure = ex; }
                    }
                }
                try { WriteJournal(journal, rollbackFailure == null ? "ROLLED_BACK" :
                    "RECOVERY_REQUIRED", project, projectSha, additions); }
                catch (Exception) { }
                if (rollbackFailure != null)
                    throw new IOException("ROLLBACK NON COMPLETATO. Backup: " + backup +
                        ". Non proseguire con altre operazioni finche non viene ripristinato.",
                        new AggregateException(cause, rollbackFailure));
                throw new IOException("Commit fallito: rollback effettuato. Backup: " + backup,
                    cause);
            }

            // The disk commit is complete. JDT LS is best-effort and cannot be part
            // of the on-disk atomicity boundary; do not roll back an already committed
            // project solely because the language server was temporarily unavailable.
            string syncNote = "Sincronizzazione JDT LS richiesta.";
            try
            {
                JavaDependencyLanguageSync.RefreshPlainProjectNow(project);
                JavaDependencyLanguageSync.RefreshAll();
            }
            catch (Exception ex)
            {
                syncNote = "AVVISO: sincronizzazione JDT LS fallita: " + ex.Message +
                    ". Ricaricare progetto/soluzione.";
                JavaJpmProjectLogger.Warning(project, VersionTag, syncNote);
            }
            try { WriteJournal(journal, "DONE", project, projectSha, additions); }
            catch (Exception ex)
            {
                JavaJpmProjectLogger.Warning(project, VersionTag,
                    "Commit eseguito, ma impossibile chiudere il journal: " + ex.Message);
            }
            var details = VersionTag + " | COMMIT ESEGUITO\n" +
                "JAR aggiunti: " + additions.Count + ". Nessun esistente sovrascritto.\n" +
                "JAR manuali preservati: " + staged.ManualJarCount + ".\n" +
                "Backup persistente: " + backup + "\n" +
                syncNote + "\n" +
                "Ripetere Analizza con Maven: attese 0 differenze.";
            return new CommitResult {
                TransactionDirectory = transaction,
                Added = additions.Count,
                Details = details
            };
        }

        private static List<Addition> ValidateAdditiveRequest(string projectFile,
            JavaPlainMavenResolutionPlanner.Plan plan,
            JavaPlainResolutionJarStaging.StageResult staged)
        {
            if (plan == null || plan.ClasspathDiffs == null || staged == null ||
                staged.NothingToStage || staged.Files == null ||
                string.IsNullOrWhiteSpace(staged.StageDirectory) ||
                !Directory.Exists(staged.StageDirectory))
                throw new InvalidOperationException("Prima eseguire Analizza, Valida transazione e Prepara JAR.");
            if (plan.Updated != 0 || plan.Removed != 0 || plan.Added < 1 || plan.Unverified != 0 ||
                plan.HasOverride)
                throw new NotSupportedException("S2D.3.3: commit limitato alle aggiunte pure. " +
                    "Aggiornamenti, rimozioni e override di versione NON sono ancora autorizzati.");
            if (plan.Added != plan.ClasspathDiffs.Count(x => x.Status == "Aggiunto"))
                throw new InvalidDataException("Numero di aggiunte incoerente nel piano.");
            string full = Path.GetFullPath(projectFile);
            var installed = JavaPackageManagerInstalledReader.Read(full);
            if (!string.Equals(installed.Backend, "Plain", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Il commit e consentito soltanto per Plain.");
            var root = Path.GetDirectoryName(full);
            var additions = new List<Addition>();
            foreach (var row in plan.ClasspathDiffs.Where(x => x.Status == "Aggiunto"))
            {
                string prefix = "Plain: ";
                string split = " | Maven: ";
                string required = row.RequiredBy ?? "";
                if (!required.StartsWith(prefix, StringComparison.Ordinal) ||
                    !required.Contains(split))
                    throw new InvalidDataException("Origine Maven ambigua: " + row.Coordinate);
                string maven = required.Substring(required.IndexOf(split, StringComparison.Ordinal) + split.Length);
                if (string.IsNullOrWhiteSpace(maven) || maven.Contains(","))
                    throw new NotSupportedException("Dipendenza condivisa da piu radici: " + row.Coordinate);
                var owners = installed.Items.Where(x => x.MavenCoordinate &&
                    string.Equals(x.Coordinate + ":" + x.Version, maven,
                        StringComparison.OrdinalIgnoreCase)).ToList();
                if (owners.Count != 1)
                    throw new InvalidDataException("Nessun proprietario Plain univoco: " + maven);
                var owner = owners[0];
                var stagedJar = staged.Files.SingleOrDefault(x =>
                    string.Equals(x.Coordinate, row.Coordinate, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Version, row.ProposedVersion, StringComparison.OrdinalIgnoreCase));
                if (stagedJar == null)
                    throw new InvalidDataException("JAR proposto assente nello staging: " + row.Coordinate);
                string[] parts = (row.Coordinate ?? "").Split(':');
                if (parts.Length != 2 || parts.Any(x => x.Length == 0))
                    throw new InvalidDataException("Coordinate non valide: " + row.Coordinate);
                if (stagedJar.FileName != parts[0] + "." + parts[1] + "-" + row.ProposedVersion + ".jar")
                    throw new InvalidDataException("Nome JAR non corrispondente alla proposta.");
                string source = Path.Combine(staged.StageDirectory, "jars", stagedJar.FileName);
                RequireSafeToken(owner.GroupId, 200);
                RequireSafeToken(owner.ArtifactId, 160);
                RequireSafeToken(owner.Version, 120);
                string target = Path.GetFullPath(Path.Combine(root, "lib", "java-packages",
                    owner.GroupId, owner.ArtifactId, owner.Version, stagedJar.FileName));
                if (!target.StartsWith(Path.GetFullPath(Path.Combine(root, "lib", "java-packages")) +
                    Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Percorso JAR fuori dalla cartella gestita.");
                additions.Add(new Addition {
                    Coordinate = row.Coordinate, Version = row.ProposedVersion,
                    Owner = owner.Coordinate, OwnerVersion = owner.Version,
                    Scope = JavaPackageScopes.Require(owner.Scope), Source = source, Target = target,
                    Sha256 = stagedJar.Sha256, Size = stagedJar.Size
                });
            }
            if (additions.Count != plan.Added ||
                additions.GroupBy(x => x.Target, StringComparer.OrdinalIgnoreCase)
                    .Any(g => g.Count() != 1))
                throw new InvalidDataException("Piano con destinazioni duplicate.");
            return additions;
        }

        private static void RequireSafeToken(string value, int max)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > max ||
                !value.All(c => (c >= 'a' && c <= 'z') ||
                    (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
                    c == '.' || c == '-' || c == '_') ||
                value == "." || value == "..")
                throw new InvalidDataException("Coordinate del proprietario non sicure.");
        }

        private static void VerifyStage(JavaPlainResolutionJarStaging.StageResult staged,
            JavaPlainMavenResolutionPlanner.Plan plan)
        {
            if (staged.Files.Count != plan.SimulatedJarCount ||
                staged.StagedJarCount != plan.SimulatedJarCount)
                throw new InvalidDataException("Lo staging non contiene tutti i JAR Maven proposti.");
            string root = Path.GetFullPath(staged.StageDirectory);
            string expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\', '/');
            if (!string.Equals(Path.GetDirectoryName(root), expectedParent,
                StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("java-vsix-s2d32-", StringComparison.Ordinal) ||
                Path.GetFileName(root).Length != "java-vsix-s2d32-".Length + 32 ||
                !Path.GetFileName(root).Skip("java-vsix-s2d32-".Length).All(Uri.IsHexDigit))
                throw new InvalidDataException("Staging fuori da TEMP o nome inatteso.");
            string jars = Path.Combine(root, "jars");
            if (!Directory.Exists(jars) ||
                (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(jars) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Directory staging non sicura.");
            string[] paths = Directory.GetFiles(jars, "*", SearchOption.TopDirectoryOnly);
            if (paths.Length != staged.Files.Count ||
                Directory.GetDirectories(jars, "*", SearchOption.TopDirectoryOnly).Length != 0)
                throw new InvalidDataException("Lo staging contiene file o cartelle inattesi.");
            foreach (var jar in staged.Files)
            {
                string path = Path.Combine(jars, jar.FileName);
                if (!File.Exists(path) ||
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                    new FileInfo(path).Length != jar.Size ||
                    !string.Equals(Sha(path), jar.Sha256, StringComparison.Ordinal))
                    throw new InvalidDataException("JAR dello staging modificato: " + jar.FileName);
            }
        }

        private static void ValidateTargets(string project, List<Addition> additions)
        {
            string root = Path.GetDirectoryName(project);
            foreach (var add in additions)
            {
                EnsureTreeSafe(root, Path.GetDirectoryName(add.Target));
                if (File.Exists(add.Target) || Directory.Exists(add.Target))
                    throw new IOException("Non sovrascrivo un JAR gia presente: " + add.Target);
            }
        }

        private static void EnsureTreeSafe(string root, string path)
        {
            string full = Path.GetFullPath(path);
            string baseRoot = Path.GetFullPath(root).TrimEnd('\\', '/');
            if (!full.StartsWith(baseRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(full, baseRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Percorso fuori dal progetto: " + path);
            if ((File.GetAttributes(baseRoot) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Progetto in junction/symlink non supportato.");
            string current = baseRoot;
            string rest = full.Substring(baseRoot.Length).TrimStart('\\', '/');
            foreach (string segment in rest.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (File.Exists(current) || Directory.Exists(current))
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Junction/symlink non supportato: " + current);
            }
        }

        private static XmlDocument ReadXml(string project)
        {
            var settings = new XmlReaderSettings { XmlResolver = null,
                DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 8 * 1024 * 1024 };
            var doc = new XmlDocument { XmlResolver = null, PreserveWhitespace = true };
            using (var reader = XmlReader.Create(project, settings)) doc.Load(reader);
            if (doc.DocumentElement == null || doc.DocumentElement.LocalName != "Project")
                throw new InvalidDataException("Elemento Project mancante.");
            return doc;
        }

        private static void AddReferences(XmlDocument doc, List<Addition> additions)
        {
            var root = doc.DocumentElement;
            string ns = root.NamespaceURI;
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in root.SelectNodes(".//*").OfType<XmlElement>()
                .Where(x => x.LocalName == "JavaJarReference"))
                existing.Add(item.GetAttribute("Include").Replace('/', '\\'));
            foreach (var add in additions)
            {
                string relative = "lib\\java-packages\\" + add.Owner.Replace(':', '\\') +
                    "\\" + add.OwnerVersion + "\\" + Path.GetFileName(add.Target);
                if (!existing.Add(relative))
                    throw new InvalidDataException("JavaJarReference duplicato: " + relative);
                var group = root.ChildNodes.OfType<XmlElement>()
                    .Where(x => x.LocalName == "ItemGroup")
                    .FirstOrDefault(x => x.ChildNodes.OfType<XmlElement>().Any(item =>
                        item.LocalName == "JavaJarReference" &&
                        string.Equals(item.ChildNodes.OfType<XmlElement>()
                            .FirstOrDefault(m => m.LocalName == JavaPlainPackageManager.ManagedId)
                            ?.InnerText, add.Owner, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(item.ChildNodes.OfType<XmlElement>()
                            .FirstOrDefault(m => m.LocalName == JavaPlainPackageManager.ManagedVersion)
                            ?.InnerText, add.OwnerVersion, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(JavaPackageScopes.Normalize(item.ChildNodes.OfType<XmlElement>()
                            .FirstOrDefault(m => m.LocalName == JavaPackageScopes.PlainMetadata)
                            ?.InnerText), add.Scope, StringComparison.OrdinalIgnoreCase)));
                if (group == null)
                    throw new InvalidDataException("ItemGroup del proprietario non trovato: " + add.Owner);
                var itemNode = doc.CreateElement("JavaJarReference", ns);
                itemNode.SetAttribute("Include", relative);
                AppendText(doc, itemNode, JavaPlainPackageManager.ManagedId, add.Owner, ns);
                AppendText(doc, itemNode, JavaPlainPackageManager.ManagedVersion, add.OwnerVersion, ns);
                AppendText(doc, itemNode, "JavaPackageManagerScope", add.Scope, ns);
                group.AppendChild(itemNode);
            }
        }

        private static void AppendText(XmlDocument doc, XmlElement parent,
            string name, string value, string ns)
        {
            XmlElement child = doc.CreateElement(name, ns);
            child.InnerText = value;
            parent.AppendChild(child);
        }

        private static bool FileHasBom(string path)
        {
            using (var file = File.OpenRead(path))
                return file.ReadByte() == 0xEF && file.ReadByte() == 0xBB &&
                    file.ReadByte() == 0xBF;
        }

        private static void WriteXml(XmlDocument doc, string file, bool bom)
        {
            var config = new XmlWriterSettings {
                Encoding = new UTF8Encoding(bom), Indent = false,
                NewLineHandling = NewLineHandling.None, CloseOutput = true
            };
            using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = XmlWriter.Create(output, config))
                doc.Save(writer);
        }

        // Never silently overwrite a previous crash/interrupted transaction.
        // Its durable backup and journal must be inspected/recovered first.
        private static void RefuseInterruptedTransactions(string root)
        {
            foreach (string directory in Directory.GetDirectories(root, "tx-*",
                SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Directory transazione con junction/symlink.");
                string name = Path.GetFileName(directory);
                if (name.Length != 35 || !name.Skip(3).All(Uri.IsHexDigit))
                    throw new InvalidDataException("Nome di transazione inatteso: " + name);
                string state = Path.Combine(directory, "transaction-state.txt");
                string first = File.Exists(state) ? File.ReadLines(state).FirstOrDefault() : null;
                if (first == null ||
                    (!first.EndsWith("| DONE", StringComparison.Ordinal) &&
                     !first.EndsWith("| ROLLED_BACK", StringComparison.Ordinal)))
                    throw new InvalidOperationException(
                        "Transazione precedente non conclusa: " + directory +
                        ". Verificare backup/journal prima di proseguire.");
            }
        }

        private static void WriteJournal(string path, string state, string project,
            string originalSha, IEnumerable<Addition> additions)
        {
            var message = new StringBuilder()
                .AppendLine(VersionTag + " | " + state)
                .AppendLine("Progetto: " + project)
                .AppendLine("SHA256 originale: " + originalSha);
            foreach (var add in additions)
                message.AppendLine("+ " + add.Target + " SHA256 " + add.Sha256);
            File.WriteAllText(path, message.ToString(), new UTF8Encoding(false));
        }

        private static string Sha(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream))
                    .Replace("-", "").ToLowerInvariant();
        }
    }
}
