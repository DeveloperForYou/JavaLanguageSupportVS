using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using JavaLanguageSupport.ProjectSystem.Dependencies;

namespace JavaLanguageSupport.ProjectSystem.PackageManager
{
    // JPM-S2D412-UPDATE-TRANSACTION-20261125
    // One OWNED TRANSITIVE version replacement, never a direct-root update.
    // The old binary is NEVER removed. A new binary is created, followed by
    // the atomic project reference switch, under the SAME mutex as S2D3.3.
    // The persistent journal is deliberately distinct from additive journals.
    internal static class JavaPlainResolutionUpdateCommit
    {
        internal const string Version = "JPM-S2D.4.1.2";
        private const string RootName = "plain-resolution-transactions";
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        internal sealed class Result
        {
            internal string Details;
            internal string TransactionDirectory;
        }
        internal sealed class RecoveryCheck
        {
            internal bool CanRecover;
            internal string Details;
        }
        private sealed class Snapshot
        {
            internal string Project;
            internal string Directory;
            internal string Journal;
            internal string OriginalSha;
            internal string ProposedSha;
            internal string OldJar;
            internal string OldSha;
            internal string NewJar;
            internal string NewSha;
            internal string State;
        }

        internal static string Preview(string project,
            JavaPlainMavenResolutionPlanner.Plan plan,
            JavaPlainResolutionTransactionPreflight.Validation prior,
            JavaPlainResolutionJarStaging.StageResult stage)
        {
            var inspected = JavaPlainResolutionUpdatePreview.Inspect(project, plan, prior, stage);
            return Version + " | CONFERMA AGGIORNAMENTO\n" +
                "Sostituire il riferimento: " + Path.GetFileName(inspected.PreviousJar) +
                " -> " + Path.GetFileName(inspected.ProposedJar) + "\n" +
                "Proprietario: " + inspected.Owner + " [" + inspected.Scope + "]\n" +
                "La vecchia versione resta fisicamente disponibile e NON viene eliminata.\n" +
                "Prima di scrivere: nuova risoluzione Maven, verifica SHA-256 completa.\n" +
                "Dopo conferma: backup, journal durevole e swap atomico del .javaproj.\n" +
                "In caso di errore: rollback; dopo crash: Recupera interrotto.\n" +
                "Nessuna verifica di compatibilita API/binaria.";
        }

        internal static async Task<Result> ApplyAsync(string projectFile,
            JavaPlainMavenResolutionPlanner.Plan plan,
            JavaPlainResolutionTransactionPreflight.Validation prior,
            JavaPlainResolutionJarStaging.StageResult stage)
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try { return await Task.Run(() => ApplyLocked(projectFile, plan, prior, stage))
                .ConfigureAwait(false); }
            finally { Gate.Release(); }
        }

        private static Result ApplyLocked(string projectFile,
            JavaPlainMavenResolutionPlanner.Plan plan,
            JavaPlainResolutionTransactionPreflight.Validation prior,
            JavaPlainResolutionJarStaging.StageResult stage)
        {
            string project = ValidateProject(projectFile);
            using (var mutex = new Mutex(false, MutexName(project)))
            {
                bool entered;
                try { entered = mutex.WaitOne(TimeSpan.Zero); }
                catch (AbandonedMutexException) { entered = true; }
                if (!entered) throw new InvalidOperationException(
                    "Un'altra istanza di Visual Studio usa questo progetto.");
                try { return ApplyInsideMutex(project, plan, prior, stage); }
                finally { mutex.ReleaseMutex(); }
            }
        }

        private static Result ApplyInsideMutex(string project,
            JavaPlainMavenResolutionPlanner.Plan plan,
            JavaPlainResolutionTransactionPreflight.Validation prior,
            JavaPlainResolutionJarStaging.StageResult stage)
        {
            // A prior crash is a hard block, not a condition to silently clean up.
            var recovery = JavaPlainResolutionCrashRecovery.Inspect(project);
            if (recovery.PendingCount != 0)
                throw new InvalidOperationException("Esiste un journal non concluso. " +
                    "Usare Verifica recovery / Recupera interrotto prima di procedere. " +
                    recovery.Details);
            var initial = JavaPlainResolutionUpdatePreview.Inspect(project, plan, prior, stage);
            var fresh = JavaPlainResolutionTransactionPreflight.PrepareAsync(
                project, plan, CancellationToken.None).GetAwaiter().GetResult();
            if (fresh.BlockingIssueCount != 0 || fresh.ChangedCount != 1 ||
                !string.Equals(fresh.ManifestFingerprint, prior.ManifestFingerprint,
                    StringComparison.Ordinal) ||
                !string.Equals(fresh.ManifestFingerprint, stage.SourceFingerprint,
                    StringComparison.Ordinal))
                throw new IOException("Il preflight finale non corrisponde allo staging.");
            VerifyStagedAll(stage, plan);
            var verified = JavaPlainResolutionUpdatePreview.Inspect(project, plan, fresh, stage);
            if (!string.Equals(initial.PreviousJar, verified.PreviousJar,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(initial.ProposedJar, verified.ProposedJar,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(initial.PreviousSha256, verified.PreviousSha256,
                    StringComparison.Ordinal) ||
                !string.Equals(initial.ProposedSha256, verified.ProposedSha256,
                    StringComparison.Ordinal))
                throw new IOException("Il piano e cambiato dopo la conferma.");

            string root = Path.GetDirectoryName(project);
            string txRoot = Path.Combine(root, ".java-vs", RootName);
            EnsureSafe(root, Path.Combine(root, ".java-vs"));
            Directory.CreateDirectory(Io(txRoot));
            EnsureSafe(root, txRoot);
            // Do not race with an external journal writer; check AFTER creating txRoot.
            recovery = JavaPlainResolutionCrashRecovery.Inspect(project);
            if (recovery.PendingCount != 0)
                throw new IOException("Un journal e stato creato durante la preparazione.");
            string transaction = Path.Combine(txRoot, "tx-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Io(transaction));
            string original = Path.Combine(transaction, "project-original.javaproj");
            string proposed = Path.Combine(transaction, "project-proposed.javaproj");
            string journal = Path.Combine(transaction, "transaction-state.txt");
            string initialHash = Sha(project);
            CopyDurably(project, original);
            if (Sha(original) != initialHash)
                throw new IOException("Backup .javaproj non verificato.");
            XmlDocument xml = ReadXml(project, true);
            ChangeOnlyReference(xml, root, verified.PreviousJar, verified.ProposedJar);
            WriteXml(xml, proposed, HasBom(project));
            string proposedHash = Sha(proposed);
            string from = verified.PreviousJar, to = verified.ProposedJar;
            string oldHash = verified.PreviousSha256, newHash = verified.ProposedSha256;
            bool journalCreated = false;
            try
            {
                WriteJournal(journal, "PREPARED", project, initialHash, proposedHash,
                    from, oldHash, to, newHash);
                journalCreated = true;
                EnsureSafe(root, to);
                if (File.Exists(Io(to)) || Directory.Exists(Io(to)))
                    throw new IOException("Destinazione JAR occupata: " + to);
                var stageJar = stage.Files.Single(x =>
                    string.Equals(x.Sha256, newHash, StringComparison.Ordinal) &&
                    string.Equals(Path.GetFileName(to), x.FileName, StringComparison.Ordinal));
                string source = Path.Combine(stage.StageDirectory, "jars", stageJar.FileName);
                string pending = to + ".jpm-" + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var input = new FileStream(Io(source), FileMode.Open,
                        FileAccess.Read, FileShare.Read))
                    using (var output = new FileStream(Io(pending), FileMode.CreateNew,
                        FileAccess.Write, FileShare.None))
                    { input.CopyTo(output); output.Flush(true); }
                    if (Sha(pending) != newHash)
                        throw new IOException("Nuovo JAR copiato con SHA-256 diverso.");
                    if (File.Exists(Io(to)))
                        throw new IOException("Il JAR di destinazione e stato creato da terzi.");
                    File.Move(Io(pending), Io(to));
                }
                finally { if (File.Exists(Io(pending))) File.Delete(Io(pending)); }
                WriteJournal(journal, "JARS_COPIED", project, initialHash, proposedHash,
                    from, oldHash, to, newHash);
                // Verify the entire source snapshot before the atomic replacement.
                if (Sha(project) != initialHash || Sha(from) != oldHash || Sha(to) != newHash ||
                    JavaPlainResolutionTransactionPreflight.CaptureFingerprint(
                        project, CancellationToken.None) != stage.SourceFingerprint)
                    throw new IOException("File modificati durante la transazione.");
                string temp = Path.Combine(root,
                    ".jpm-update-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    CopyDurably(proposed, temp);
                    if (Sha(temp) != proposedHash)
                        throw new IOException("Proposta .javaproj alterata.");
                    File.Replace(Io(temp), Io(project), null, true);
                }
                finally { if (File.Exists(Io(temp))) File.Delete(Io(temp)); }
                WriteJournal(journal, "PROJECT_COMMITTED", project, initialHash, proposedHash,
                    from, oldHash, to, newHash);
                if (Sha(project) != proposedHash || Sha(from) != oldHash || Sha(to) != newHash)
                    throw new IOException("Verifica post-commit fallita.");
            }
            catch (Exception error)
            {
                if (!journalCreated)
                    throw new IOException("Transazione non inizializzata: controllare " +
                        "la directory " + transaction + ". Nessun ripristino automatico tentato.", error);
                try { RecoverVerified(project, transaction); }
                catch (Exception rollbackError)
                {
                    try { WriteJournal(journal, "RECOVERY_REQUIRED", project,
                        initialHash, proposedHash, from, oldHash, to, newHash); }
                    catch (Exception) { }
                    throw new IOException("ROLLBACK NON COMPLETATO. Non cancellare backup/journal: " +
                        transaction, new AggregateException(error, rollbackError));
                }
                throw new IOException("Aggiornamento fallito: rollback completato. Backup: " +
                    original, error);
            }
            string sync = "Aggiornamento JDT LS richiesto.";
            try
            {
                JavaDependencyLanguageSync.RefreshPlainProjectNow(project);
                JavaDependencyLanguageSync.RefreshAll();
            }
            catch (Exception ex)
            {
                sync = "AVVISO JDT LS: " + ex.Message + ". Ricaricare il progetto.";
                JavaJpmProjectLogger.Warning(project, Version, sync);
            }
            try { WriteJournal(journal, "DONE", project, initialHash,
                proposedHash, from, oldHash, to, newHash); }
            catch (Exception ex)
            {
                JavaJpmProjectLogger.Warning(project, Version,
                    "Commit sul disco riuscito, journal non chiuso: " + ex.Message);
            }
            return new Result { TransactionDirectory = transaction,
                Details = Version + " | AGGIORNAMENTO COMPLETATO\n" +
                    Path.GetFileName(from) + " -> " + Path.GetFileName(to) + "\n" +
                    "Vecchio JAR conservato (non piu referenziato): " + from + "\n" +
                    "Backup: " + original + "\n" + sync +
                    "\nRieseguire Analizza con Maven: attese 0 differenze." };
        }

        // Called by S2D3.5 only AFTER acquiring the shared cross-process mutex.
        internal static RecoveryCheck InspectInterrupted(string project, string transaction)
        {
            try
            {
                Snapshot s = LoadAndVerify(project, transaction);
                return new RecoveryCheck { CanRecover = true,
                    Details = Version + " | INTERROTTO: " + s.State + "\n" +
                        "Backup e proposta SHA-256 verificati; JAR precedente intatto.\n" +
                        "Il nuovo JAR verra eliminato soltanto dopo il ripristino .javaproj.\n" +
                        "Vecchio JAR conservato: " + s.OldJar + "\n" +
                        "Backup: " + Path.Combine(s.Directory, "project-original.javaproj") };
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                return new RecoveryCheck { CanRecover = false,
                    Details = "RECUPERO BLOCCATO, nessun file modificato: " + ex.Message };
            }
        }

        internal static string RecoverInterruptedUnderLock(string project, string transaction)
        {
            RecoverVerified(project, transaction);
            return Version + " | RECUPERO CONCLUSO: ROLLED_BACK. " +
                "Vecchia versione del JAR conservata; nuova versione rimossa. Journal: " +
                Path.Combine(transaction, "transaction-state.txt");
        }

        private static void RecoverVerified(string project, string transaction)
        {
            Snapshot s = LoadAndVerify(project, transaction);
            string root = Path.GetDirectoryName(project);
            string backup = Path.Combine(transaction, "project-original.javaproj");
            string current = Sha(project);
            if (current != s.OriginalSha && current != s.ProposedSha)
                throw new IOException("Progetto modificato dopo crash: nessun overwrite autorizzato.");
            if (current != s.OriginalSha)
            {
                string temp = Path.Combine(root,
                    ".jpm-update-recovery-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    CopyDurably(backup, temp);
                    if (Sha(temp) != s.OriginalSha)
                        throw new IOException("Backup differente durante il recovery.");
                    File.Replace(Io(temp), Io(project), null, true);
                }
                finally { if (File.Exists(Io(temp))) File.Delete(Io(temp)); }
            }
            if (Sha(project) != s.OriginalSha || Sha(s.OldJar) != s.OldSha)
                throw new IOException("Verifica base dopo il ripristino non riuscita.");
            if (File.Exists(Io(s.NewJar)))
            {
                if (Sha(s.NewJar) != s.NewSha)
                    throw new IOException("Nuovo JAR modificato durante il recovery.");
                File.Delete(Io(s.NewJar));
            }
            if (File.Exists(Io(s.NewJar)) || Sha(project) != s.OriginalSha)
                throw new IOException("Rollback non completo.");
            WriteJournal(s.Journal, "ROLLED_BACK", s.Project, s.OriginalSha,
                s.ProposedSha, s.OldJar, s.OldSha, s.NewJar, s.NewSha);
        }

        private static Snapshot LoadAndVerify(string projectFile, string transaction)
        {
            string project = ValidateProject(projectFile);
            string root = Path.GetDirectoryName(project);
            string expectedRoot = Path.GetFullPath(Path.Combine(root, ".java-vs", RootName));
            string directory = Path.GetFullPath(transaction);
            string name = Path.GetFileName(directory);
            if (!string.Equals(Path.GetDirectoryName(directory), expectedRoot,
                    StringComparison.OrdinalIgnoreCase) || name.Length != 35 ||
                !name.StartsWith("tx-", StringComparison.Ordinal) ||
                !name.Skip(3).All(Uri.IsHexDigit))
                throw new InvalidDataException("Directory journal non valida.");
            EnsureSafe(root, directory);
            string journal = Path.Combine(directory, "transaction-state.txt");
            string backup = Path.Combine(directory, "project-original.javaproj");
            string proposed = Path.Combine(directory, "project-proposed.javaproj");
            foreach (string p in new[] { journal, backup, proposed })
            {
                if (!File.Exists(Io(p))) throw new FileNotFoundException("Journal/backup assente", p);
                EnsureSafe(root, p);
            }
            if (new FileInfo(Io(journal)).Length > 8192)
                throw new InvalidDataException("Journal aggiornamento troppo grande.");
            string[] lines = File.ReadAllLines(Io(journal), Encoding.UTF8);
            if (lines.Length != 6 || !lines[0].StartsWith(Version + " | ", StringComparison.Ordinal))
                throw new InvalidDataException("Journal aggiornamento sconosciuto.");
            string state = lines[0].Substring((Version + " | ").Length);
            if (state != "PREPARED" && state != "JARS_COPIED" &&
                state != "PROJECT_COMMITTED" && state != "RECOVERY_REQUIRED")
                throw new InvalidDataException("Stato journal non recuperabile: " + state);
            if (lines[1] != "Progetto: " + project ||
                !lines[2].StartsWith("SHA256 originale: ", StringComparison.Ordinal) ||
                !lines[3].StartsWith("SHA256 proposto: ", StringComparison.Ordinal))
                throw new InvalidDataException("Journal non associato al progetto corrente.");
            string oldImage = lines[2].Substring("SHA256 originale: ".Length);
            string newImage = lines[3].Substring("SHA256 proposto: ".Length);
            if (!IsSha(oldImage) || !IsSha(newImage) || Sha(backup) != oldImage ||
                Sha(proposed) != newImage)
                throw new IOException("Backup/proposta SHA-256 non validi.");
            string oldJar, oldSha, newJar, newSha;
            ParseJarLine(lines[4], "- ", out oldJar, out oldSha);
            ParseJarLine(lines[5], "+ ", out newJar, out newSha);
            if (string.Equals(oldJar, newJar, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetDirectoryName(oldJar), Path.GetDirectoryName(newJar),
                    StringComparison.OrdinalIgnoreCase) ||
                !IsBelow(Path.Combine(root, "lib", "java-packages"), oldJar) ||
                !IsBelow(Path.Combine(root, "lib", "java-packages"), newJar))
                throw new InvalidDataException("JAR non sotto lo stesso proprietario Plain.");
            EnsureSafe(root, oldJar);
            EnsureSafe(root, newJar);
            if (!File.Exists(Io(oldJar)) || Sha(oldJar) != oldSha)
                throw new IOException("JAR della vecchia versione alterato: blocco.");
            if (File.Exists(Io(newJar)) && Sha(newJar) != newSha)
                throw new IOException("JAR della nuova versione alterato: blocco.");
            VerifyProposedOnlyReference(backup, proposed, root, oldJar, newJar);
            string current = Sha(project);
            if (current != oldImage && current != newImage)
                throw new IOException("Il progetto e stato modificato dopo il crash.");
            return new Snapshot { Project = project, Directory = directory,
                Journal = journal, State = state, OriginalSha = oldImage,
                ProposedSha = newImage, OldJar = oldJar, OldSha = oldSha,
                NewJar = newJar, NewSha = newSha };
        }

        private static void VerifyProposedOnlyReference(string backup, string proposed,
            string root, string oldJar, string newJar)
        {
            XmlDocument first = ReadXml(backup, false);
            XmlDocument second = ReadXml(proposed, false);
            string oldRel = oldJar.Substring(root.Length).TrimStart('\\', '/').Replace('/', '\\');
            string newRel = newJar.Substring(root.Length).TrimStart('\\', '/').Replace('/', '\\');
            var originals = first.DocumentElement.SelectNodes(".//*").OfType<XmlElement>()
                .Where(x => x.LocalName == "JavaJarReference" &&
                    string.Equals(x.GetAttribute("Include").Replace('/', '\\'), oldRel,
                        StringComparison.OrdinalIgnoreCase)).ToArray();
            if (originals.Length != 1)
                throw new InvalidDataException("Riferimento precedente non univoco.");
            var existingNew = first.DocumentElement.SelectNodes(".//*").OfType<XmlElement>()
                .Where(x => x.LocalName == "JavaJarReference" &&
                    string.Equals(x.GetAttribute("Include").Replace('/', '\\'), newRel,
                        StringComparison.OrdinalIgnoreCase)).ToArray();
            if (existingNew.Length != 0)
                throw new InvalidDataException("Nuovo JAR gia referenziato nell'originale.");
            XmlElement old = originals[0];
            string owner = old.ChildNodes.OfType<XmlElement>()
                .Where(x => x.LocalName == JavaPlainPackageManager.ManagedId)
                .Select(x => x.InnerText.Trim()).FirstOrDefault();
            string ownerVersion = old.ChildNodes.OfType<XmlElement>()
                .Where(x => x.LocalName == JavaPlainPackageManager.ManagedVersion)
                .Select(x => x.InnerText.Trim()).FirstOrDefault();
            string scope = old.ChildNodes.OfType<XmlElement>()
                .Where(x => x.LocalName == JavaPackageScopes.PlainMetadata)
                .Select(x => x.InnerText.Trim()).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(ownerVersion) ||
                !JavaPackageScopes.IsSupported(scope))
                throw new InvalidDataException("Metadata proprietario del JAR assenti/invalidi.");
            string[] parts = oldRel.Split('\\');
            if (parts.Length != 6 || parts[0] != "lib" || parts[1] != "java-packages" ||
                !string.Equals(owner, parts[2] + ":" + parts[3],
                    StringComparison.OrdinalIgnoreCase) || ownerVersion != parts[4])
                throw new InvalidDataException("Percorso proprietario JAR incoerente.");
            old.SetAttribute("Include", newRel);
            if (!string.Equals(first.DocumentElement.OuterXml,
                second.DocumentElement.OuterXml, StringComparison.Ordinal))
                throw new InvalidDataException("Proposta cambia piu del singolo Include autorizzato.");
        }

        private static void ChangeOnlyReference(XmlDocument xml, string root,
            string oldJar, string newJar)
        {
            string oldRel = oldJar.Substring(root.Length).TrimStart('\\', '/').Replace('/', '\\');
            string newRel = newJar.Substring(root.Length).TrimStart('\\', '/').Replace('/', '\\');
            var elements = xml.DocumentElement.SelectNodes(".//*").OfType<XmlElement>()
                .Where(x => x.LocalName == "JavaJarReference").ToArray();
            var matched = elements.Where(x => string.Equals(x.GetAttribute("Include").Replace('/', '\\'),
                oldRel, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matched.Length != 1 || elements.Any(x => string.Equals(
                x.GetAttribute("Include").Replace('/', '\\'), newRel,
                StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Include vecchio/nuovo non univoco.");
            matched[0].SetAttribute("Include", newRel);
        }

        private static void VerifyStagedAll(JavaPlainResolutionJarStaging.StageResult stage,
            JavaPlainMavenResolutionPlanner.Plan plan)
        {
            if (stage.Files == null || stage.Files.Count != plan.SimulatedJarCount ||
                stage.StagedJarCount != plan.SimulatedJarCount ||
                !Directory.Exists(Io(stage.StageDirectory)))
                throw new InvalidDataException("Manifest staging incompleto.");
            string jars = Path.Combine(stage.StageDirectory, "jars");
            EnsureSafe(stage.StageDirectory, jars);
            if (!Directory.Exists(Io(jars)) ||
                Directory.GetFiles(Io(jars), "*", SearchOption.TopDirectoryOnly).Length != stage.Files.Count ||
                Directory.GetDirectories(Io(jars), "*", SearchOption.TopDirectoryOnly).Length != 0)
                throw new InvalidDataException("Staging con file inattesi.");
            foreach (var file in stage.Files)
            {
                string path = Path.Combine(jars, file.FileName);
                EnsureSafe(stage.StageDirectory, path);
                if (!File.Exists(Io(path)) || new FileInfo(Io(path)).Length != file.Size ||
                    Sha(path) != file.Sha256)
                    throw new IOException("JAR dello staging modificato: " + file.FileName);
            }
        }

        private static string ValidateProject(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                throw new ArgumentException("Nessun .javaproj selezionato.");
            string project = Path.GetFullPath(input);
            if (!string.Equals(Path.GetExtension(project), ".javaproj",
                StringComparison.OrdinalIgnoreCase) || !File.Exists(Io(project)))
                throw new FileNotFoundException("Progetto Java Plain assente.", project);
            EnsureSafe(Path.GetDirectoryName(project), project);
            return project;
        }
        private static void EnsureSafe(string root, string path)
        {
            string baseRoot = Path.GetFullPath(root).TrimEnd('\\', '/');
            string full = Path.GetFullPath(path);
            if (!string.Equals(baseRoot, full, StringComparison.OrdinalIgnoreCase) &&
                !IsBelow(baseRoot, full))
                throw new InvalidDataException("Percorso fuori dalla directory consentita.");
            if ((File.GetAttributes(Io(baseRoot)) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Root con junction/symlink.");
            string current = baseRoot;
            foreach (string part in full.Substring(baseRoot.Length).Split(
                new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if ((File.Exists(Io(current)) || Directory.Exists(Io(current))) &&
                    (File.GetAttributes(Io(current)) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Symlink/junction non ammessa: " + current);
            }
        }
        private static bool IsBelow(string root, string path)
        {
            return Path.GetFullPath(path).StartsWith(
                Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        private static string Io(string path)
        {
            string full = Path.GetFullPath(path);
            return Path.DirectorySeparatorChar == '\\' &&
                !full.StartsWith(@"\\?\", StringComparison.Ordinal) ? @"\\?\" + full : full;
        }
        private static string Sha(string path)
        {
            using (var file = new FileStream(Io(path), FileMode.Open,
                FileAccess.Read, FileShare.Read))
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "")
                    .ToLowerInvariant();
        }
        private static bool IsSha(string hash)
        {
            return hash != null && hash.Length == 64 &&
                hash.All(x => (x >= '0' && x <= '9') || (x >= 'a' && x <= 'f'));
        }
        private static bool HasBom(string file)
        {
            using (var stream = new FileStream(Io(file), FileMode.Open,
                FileAccess.Read, FileShare.Read))
                return stream.ReadByte() == 0xef && stream.ReadByte() == 0xbb &&
                    stream.ReadByte() == 0xbf;
        }
        private static XmlDocument ReadXml(string file, bool preserveWhitespace)
        {
            var settings = new XmlReaderSettings { XmlResolver = null,
                DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 8 * 1024 * 1024 };
            var xml = new XmlDocument { XmlResolver = null,
                PreserveWhitespace = preserveWhitespace };
            using (var stream = new FileStream(Io(file), FileMode.Open,
                FileAccess.Read, FileShare.Read))
            using (var reader = XmlReader.Create(stream, settings)) xml.Load(reader);
            if (xml.DocumentElement == null || xml.DocumentElement.LocalName != "Project")
                throw new InvalidDataException("XML .javaproj non valido.");
            return xml;
        }
        private static void WriteXml(XmlDocument xml, string path, bool bom)
        {
            var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(bom),
                Indent = false, NewLineHandling = NewLineHandling.None, CloseOutput = false };
            using (var stream = new FileStream(Io(path), FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            {
                using (var writer = XmlWriter.Create(stream, settings))
                { xml.Save(writer); writer.Flush(); }
                stream.Flush(true);
            }
        }
        private static void CopyDurably(string input, string output)
        {
            using (var src = new FileStream(Io(input), FileMode.Open,
                FileAccess.Read, FileShare.Read))
            using (var dst = new FileStream(Io(output), FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            { src.CopyTo(dst); dst.Flush(true); }
        }
        private static void ParseJarLine(string line, string prefix,
            out string path, out string hash)
        {
            const string sep = " SHA256 ";
            int i = line.LastIndexOf(sep, StringComparison.Ordinal);
            if (!line.StartsWith(prefix, StringComparison.Ordinal) || i <= prefix.Length)
                throw new InvalidDataException("Record JAR del journal non valido.");
            path = line.Substring(prefix.Length, i - prefix.Length);
            hash = line.Substring(i + sep.Length);
            if (!IsSha(hash) || !Path.IsPathRooted(path) ||
                !string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase) ||
                !path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Percorso/checksum JAR non valido.");
        }
        private static void WriteJournal(string path, string state, string project,
            string original, string proposed, string oldJar, string oldHash,
            string newJar, string newHash)
        {
            var data = new StringBuilder().AppendLine(Version + " | " + state)
                .AppendLine("Progetto: " + project)
                .AppendLine("SHA256 originale: " + original)
                .AppendLine("SHA256 proposto: " + proposed)
                .AppendLine("- " + oldJar + " SHA256 " + oldHash)
                .AppendLine("+ " + newJar + " SHA256 " + newHash).ToString();
            string temp = Path.Combine(Path.GetDirectoryName(path),
                ".jpm-journal-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                var buffer = new UTF8Encoding(false).GetBytes(data);
                using (var stream = new FileStream(Io(temp), FileMode.CreateNew,
                    FileAccess.Write, FileShare.None))
                { stream.Write(buffer, 0, buffer.Length); stream.Flush(true); }
                if (File.Exists(Io(path))) File.Replace(Io(temp), Io(path), null, true);
                else File.Move(Io(temp), Io(path));
            }
            finally { if (File.Exists(Io(temp))) File.Delete(Io(temp)); }
        }
        private static string MutexName(string project)
        {
            using (var hash = SHA256.Create())
                return @"Local\JavaLanguageSupportVS-JPM-S2D33-" +
                    BitConverter.ToString(hash.ComputeHash(
                        Encoding.UTF8.GetBytes(project.ToUpperInvariant()))).Replace("-", "");
        }
        private static bool IsExpected(Exception ex)
        {
            return ex is IOException || ex is InvalidOperationException ||
                ex is UnauthorizedAccessException || ex is ArgumentException ||
                ex is XmlException || ex is NotSupportedException;
        }
    }
}
