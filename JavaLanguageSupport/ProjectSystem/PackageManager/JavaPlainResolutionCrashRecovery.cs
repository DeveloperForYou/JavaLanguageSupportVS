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
    // JPM-S2D35-CRASH-RECOVERY-20261125
    // A write-ahead, SHA-pinned transaction can be rolled back after a process
    // crash. Inspection is read-only. Recovery is opt-in and fail-closed: never
    // overwrite a project modified after the commit, or delete an unknown JAR.
    internal static class JavaPlainResolutionCrashRecovery
    {
        private const string JournalRootName = "plain-resolution-transactions";
        private const string Version = "JPM-S2D.3.3";
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;

        internal sealed class Report
        {
            internal int PendingCount;
            internal bool CanRecover;
            internal string TransactionDirectory;
            internal string Details;
        }

        private sealed class Pending
        {
            internal string Directory;
            internal string Journal;
            internal string Project;
            internal string OriginalSha;
            internal string ProposedSha;
            internal string State;
            internal readonly List<NewJar> Jars = new List<NewJar>();
        }

        private sealed class NewJar
        {
            internal string Path;
            internal string Sha;
        }

        internal static Report Inspect(string projectFile)
        {
            string project = ValidateProject(projectFile);
            string root = Path.GetDirectoryName(project);
            string transactions = Path.Combine(root, ".java-vs", JournalRootName);
            // JPM-S2D352-SCAN-ROOT-DIAGNOSTICS-20261125:
            // Include the exact selected project/journal root in a zero-result
            // inspection. This is read-only and does not authorize recovery.
            if (!Directory.Exists(Io(transactions)))
                return new Report { PendingCount = 0, CanRecover = false,
                    Details = "Nessuna transazione interrotta. Progetto scansionato: " +
                        project + "; cartella journal INESISTENTE: " + transactions };
            try
            {
                EnsureSafe(root, transactions);
                // Enumerate all direct children first, then filter tx-* by
                // exact name. Avoid relying on the OS wildcard matcher with
                // extended Windows paths (\\?\) and keep discovery scoped.
                var directories = Directory.GetDirectories(Io(transactions), "*",
                    SearchOption.TopDirectoryOnly)
                    .Where(x => Path.GetFileName(StripExtended(x)).StartsWith("tx-",
                        StringComparison.OrdinalIgnoreCase)).ToArray();
                var pending = new List<string>();
                foreach (string directory in directories)
                {
                    string tx = StripExtended(directory);
                    EnsureSafe(root, tx);
                    string txName = Path.GetFileName(tx);
                    if (txName.Length != 35 || !txName.StartsWith("tx-", StringComparison.Ordinal) ||
                        !txName.Skip(3).All(Uri.IsHexDigit))
                        return Blocked(pending.Count + 1, tx, "Nome transazione inatteso: " + txName);
                    string journal = Path.Combine(tx, "transaction-state.txt");
                    if (!File.Exists(Io(journal)))
                    {
                        pending.Add(tx); // Crash before the first journal: manual review.
                        continue;
                    }
                    if ((File.GetAttributes(Io(journal)) & FileAttributes.ReparsePoint) != 0)
                        return Blocked(pending.Count + 1, tx, "Journal con reparse point.");
                    string first = File.ReadLines(Io(journal)).FirstOrDefault();
                    if (first != null && (first.EndsWith("| DONE", StringComparison.Ordinal) ||
                        first.EndsWith("| ROLLED_BACK", StringComparison.Ordinal)))
                        continue;
                    pending.Add(tx);
                }
                if (pending.Count == 0)
                    return new Report { PendingCount = 0, CanRecover = false,
                        Details = "Nessuna transazione interrotta. Progetto scansionato: " +
                            project + "; cartella journal: " + transactions +
                            "; cartelle tx trovate: " + directories.Length +
                            " (tutte concluse o assenti)." };
                if (pending.Count != 1)
                    return Blocked(pending.Count, null,
                        "Piu transazioni incomplete: verifica manuale richiesta; nessuna modifica eseguita.");
                string directoryToRecover = pending[0];
                try
                {
                    // JPM-S2D412-UPDATE-TRANSACTION-20261125: updates have a
                    // separate immutable two-JAR journal. Do not parse it as
                    // an additive-only S2D3.3 transaction.
                    string updateJournal = Path.Combine(directoryToRecover,
                        "transaction-state.txt");
                    string updateHeader = File.Exists(Io(updateJournal)) ?
                        File.ReadLines(Io(updateJournal)).FirstOrDefault() : null;
                    if (updateHeader != null && updateHeader.StartsWith(
                        JavaPlainResolutionUpdateCommit.Version + " | ", StringComparison.Ordinal))
                    {
                        var update = JavaPlainResolutionUpdateCommit.InspectInterrupted(
                            project, directoryToRecover);
                        if (!update.CanRecover)
                            return Blocked(1, directoryToRecover, update.Details);
                        return new Report {
                            PendingCount = 1, CanRecover = true,
                            TransactionDirectory = directoryToRecover,
                            Details = update.Details
                        };
                    }
                    // JPM-S2D421-REMOVAL-TRANSACTION-20261126: separate unlink-only journal.
                    if (updateHeader != null && updateHeader.StartsWith(
                        JavaPlainResolutionRemovalCommit.Version + " | ", StringComparison.Ordinal))
                    {
                        var removed = JavaPlainResolutionRemovalCommit.InspectInterrupted(
                            project, directoryToRecover);
                        if (!removed.CanRecover)
                            return Blocked(1, directoryToRecover, removed.Details);
                        return new Report { PendingCount = 1, CanRecover = true,
                            TransactionDirectory = directoryToRecover, Details = removed.Details };
                    }
                    var snapshot = LoadAndVerify(project, directoryToRecover);
                    return new Report {
                        PendingCount = 1, CanRecover = true,
                        TransactionDirectory = directoryToRecover,
                        Details = "Transazione interrotta " + snapshot.State + " (" +
                            snapshot.Jars.Count + " JAR previsti).\n" +
                            "Progetto e backup verificati con SHA-256.\n" +
                            "Ogni JAR presente corrisponde al manifest.\n" +
                            "Con conferma: ripristino .javaproj, eliminazione dei SOLI JAR del manifest, " +
                            "aggiornamento JDT LS.\nBackup: " +
                            Path.Combine(directoryToRecover, "project-original.javaproj")
                    };
                }
                catch (Exception ex) when (IsExpected(ex))
                {
                    return Blocked(1, directoryToRecover, ex.Message);
                }
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                return Blocked(1, transactions, ex.Message);
            }
        }

        private static Report Blocked(int count, string directory, string reason)
        {
            return new Report {
                PendingCount = count, CanRecover = false,
                TransactionDirectory = directory,
                Details = "RECUPERO BLOCCATO. Nessun file modificato. " + reason +
                    "\nConservare i backup e verificare manualmente prima di altri commit."
            };
        }

        internal static async Task<string> RecoverAsync(string projectFile,
            string expectedDirectory)
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await Task.Run(() => RecoverLocked(projectFile, expectedDirectory))
                    .ConfigureAwait(false);
            }
            finally { Gate.Release(); }
        }

        private static string RecoverLocked(string projectFile, string expectedDirectory)
        {
            string project = ValidateProject(projectFile);
            string mutexKey;
            using (var sha = SHA256.Create())
                mutexKey = BitConverter.ToString(sha.ComputeHash(
                    Encoding.UTF8.GetBytes(project.ToUpperInvariant()))).Replace("-", "");
            // Use the SAME named mutex used by the S2D.3.3 commit engine.
            using (var mutex = new Mutex(false, @"Local\JavaLanguageSupportVS-JPM-S2D33-" + mutexKey))
            {
                bool entered;
                try { entered = mutex.WaitOne(TimeSpan.Zero); }
                catch (AbandonedMutexException) { entered = true; }
                if (!entered)
                    throw new InvalidOperationException("Un'altra istanza VS utilizza questo progetto.");
                try
                {
                    Report report = Inspect(project); // Recheck under lock, not just in UI.
                    if (report.PendingCount != 1 || !report.CanRecover ||
                        !Paths.Equals(Path.GetFullPath(expectedDirectory), report.TransactionDirectory))
                        throw new InvalidOperationException("Lo stato transazionale e cambiato: " +
                            report.Details);
                    // JPM-S2D412-UPDATE-TRANSACTION-20261125: shared mutex is
                    // already held here. The update recovery restores the old
                    // .javaproj and deletes ONLY the new SHA-pinned JAR.
                    string selectedJournal = Path.Combine(report.TransactionDirectory,
                        "transaction-state.txt");
                    string selectedHeader = File.ReadLines(Io(selectedJournal)).FirstOrDefault();
                    if (selectedHeader != null && selectedHeader.StartsWith(
                        JavaPlainResolutionUpdateCommit.Version + " | ", StringComparison.Ordinal))
                    {
                        string recovered = JavaPlainResolutionUpdateCommit.RecoverInterruptedUnderLock(
                            project, report.TransactionDirectory);
                        try
                        {
                            JavaDependencyLanguageSync.RefreshPlainProjectNow(project);
                            JavaDependencyLanguageSync.RefreshAll();
                        }
                        catch (Exception ex)
                        {
                            recovered += "\nAVVISO JDT LS: " + ex.Message;
                        }
                        return recovered;
                    }
                    // JPM-S2D421-REMOVAL-TRANSACTION-20261126: same mutex as S2D3.3.
                    if (selectedHeader != null && selectedHeader.StartsWith(
                        JavaPlainResolutionRemovalCommit.Version + " | ", StringComparison.Ordinal))
                    {
                        string restored = JavaPlainResolutionRemovalCommit.RecoverInterruptedUnderLock(
                            project, report.TransactionDirectory);
                        try
                        {
                            JavaDependencyLanguageSync.RefreshPlainProjectNow(project);
                            JavaDependencyLanguageSync.RefreshAll();
                        }
                        catch (Exception ex) { restored += "\nAVVISO JDT LS: " + ex.Message; }
                        return restored;
                    }
                    Pending tx = LoadAndVerify(project, report.TransactionDirectory);
                    string root = Path.GetDirectoryName(project);
                    string backup = Path.Combine(tx.Directory, "project-original.javaproj");
                    string projectSha = Sha(project);
                    if (!string.Equals(projectSha, tx.OriginalSha, StringComparison.Ordinal) &&
                        !string.Equals(projectSha, tx.ProposedSha, StringComparison.Ordinal))
                        throw new IOException("Il .javaproj e cambiato dopo la verifica: non lo sovrascrivo.");
                    if (!string.Equals(projectSha, tx.OriginalSha, StringComparison.Ordinal))
                    {
                        // This is an atomic restore. Do not delete ANY binaries before
                        // the project is proven restored from the durable backup.
                        string restore = Path.Combine(root,
                            ".jpm-crash-recovery-" + Guid.NewGuid().ToString("N") + ".tmp");
                        try
                        {
                            File.Copy(Io(backup), Io(restore), false);
                            if (!EqualHash(restore, tx.OriginalSha))
                                throw new IOException("Backup alterato durante la copia.");
                            File.Replace(Io(restore), Io(project), null, true);
                        }
                        finally { if (File.Exists(Io(restore))) File.Delete(Io(restore)); }
                    }
                    if (!EqualHash(project, tx.OriginalSha))
                        throw new IOException("Il progetto non corrisponde al backup dopo il ripristino.");
                    foreach (var jar in tx.Jars)
                    {
                        EnsureSafe(root, jar.Path);
                        if (!File.Exists(Io(jar.Path))) continue; // Retry after partial recovery.
                        if (!EqualHash(jar.Path, jar.Sha))
                            throw new IOException("JAR modificato durante il ripristino: " + jar.Path);
                        File.Delete(Io(jar.Path));
                    }
                    // An interrupted cleanup remains retryable: state is NOT closed
                    // until the exact preimage and absence of additions are proven.
                    if (!EqualHash(project, tx.OriginalSha) ||
                        tx.Jars.Any(x => File.Exists(Io(x.Path))))
                        throw new IOException("Verifica finale recovery non superata.");
                    var lines = File.ReadAllLines(Io(tx.Journal), Encoding.UTF8);
                    if (lines.Length < 5 || !string.Equals(lines[0],
                        Version + " | " + tx.State, StringComparison.Ordinal))
                        throw new IOException("Journal cambiato durante il recupero.");
                    lines[0] = Version + " | ROLLED_BACK";
                    DurableWrite(tx.Journal, string.Join(Environment.NewLine, lines) +
                        Environment.NewLine);
                    string note = "JPM-S2D.3.5 | RECUPERO CONCLUSO: ROLLED_BACK\n" +
                        "SHA-256 progetto ripristinato, " + tx.Jars.Count +
                        " destinazioni verificate/bonificate.\nJournal: " + tx.Journal;
                    try
                    {
                        JavaDependencyLanguageSync.RefreshPlainProjectNow(project);
                        JavaDependencyLanguageSync.RefreshAll();
                    }
                    catch (Exception ex)
                    {
                        note += "\nAVVISO JDT LS: " + ex.Message +
                            ". Ricaricare il progetto.";
                    }
                    return note;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        private static Pending LoadAndVerify(string project, string transaction)
        {
            string root = Path.GetDirectoryName(project);
            string transactionRoot = Path.GetFullPath(Path.Combine(root, ".java-vs", JournalRootName));
            string dir = Path.GetFullPath(transaction);
            if (!Paths.Equals(Path.GetDirectoryName(dir), transactionRoot) ||
                !Path.GetFileName(dir).StartsWith("tx-", StringComparison.Ordinal) ||
                Path.GetFileName(dir).Length != 35 ||
                !Path.GetFileName(dir).Skip(3).All(Uri.IsHexDigit))
                throw new InvalidDataException("Directory transazionale inattesa.");
            EnsureSafe(root, dir);
            string journal = Path.Combine(dir, "transaction-state.txt");
            string backup = Path.Combine(dir, "project-original.javaproj");
            string proposed = Path.Combine(dir, "project-proposed.javaproj");
            foreach (string p in new[] { journal, backup, proposed })
            {
                if (!File.Exists(Io(p)) ||
                    (File.GetAttributes(Io(p)) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Journal o backup/proposta assente/non sicuro: " + p);
            }
            if (new FileInfo(Io(journal)).Length > 65536)
                throw new InvalidDataException("Journal troppo grande.");
            string[] lines = File.ReadAllLines(Io(journal), Encoding.UTF8);
            if (lines.Length < 5 || lines.Length > 90 ||
                !lines[0].StartsWith(Version + " | ", StringComparison.Ordinal))
                throw new InvalidDataException("Journal non riconosciuto.");
            string state = lines[0].Substring((Version + " | ").Length);
            if (state != "PREPARED" && state != "JARS_COPIED" &&
                state != "PROJECT_COMMITTED" && state != "RECOVERY_REQUIRED")
                throw new InvalidDataException("Stato non recuperabile: " + state);
            string projectHeader = "Progetto: ";
            string originalHeader = "SHA256 originale: ";
            string proposedHeader = "SHA256 proposto: ";
            if (!lines[1].StartsWith(projectHeader, StringComparison.Ordinal) ||
                !Paths.Equals(lines[1].Substring(projectHeader.Length), project) ||
                !lines[2].StartsWith(originalHeader, StringComparison.Ordinal) ||
                !lines[3].StartsWith(proposedHeader, StringComparison.Ordinal))
                throw new InvalidDataException("Journal riferito a un altro progetto o versione precedente.");
            string originalSha = lines[2].Substring(originalHeader.Length);
            string proposedSha = lines[3].Substring(proposedHeader.Length);
            if (!IsSha(originalSha) || !IsSha(proposedSha) ||
                !EqualHash(backup, originalSha) || !EqualHash(proposed, proposedSha))
                throw new InvalidDataException("Backup/proposta non corrisponde al manifest SHA-256.");
            string managedRoot = Path.Combine(root, "lib", "java-packages");
            var items = new List<NewJar>();
            var seen = new HashSet<string>(Paths);
            for (int i = 4; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                const string prefix = "+ ";
                const string between = " SHA256 ";
                int split = line.LastIndexOf(between, StringComparison.Ordinal);
                if (!line.StartsWith(prefix, StringComparison.Ordinal) || split <= 2)
                    throw new InvalidDataException("Voce journal inattesa.");
                string target = line.Substring(prefix.Length, split - prefix.Length);
                string sha = line.Substring(split + between.Length);
                if (!IsSha(sha) || !Path.IsPathRooted(target) ||
                    !Paths.Equals(Path.GetFullPath(target), target) ||
                    !IsBelow(managedRoot, target) || !target.EndsWith(".jar",
                        StringComparison.OrdinalIgnoreCase) || !seen.Add(target))
                    throw new InvalidDataException("Destinazione JAR non sicura/duplicata.");
                EnsureSafe(root, target);
                if (File.Exists(Io(target)) && !EqualHash(target, sha))
                    throw new IOException("JAR di destinazione modificato: " + target);
                items.Add(new NewJar { Path = target, Sha = sha });
            }
            if (items.Count < 1 || items.Count > 80)
                throw new InvalidDataException("Numero aggiunte inatteso nel journal.");
            VerifyProposedXml(backup, proposed, root, items);
            string currentSha = Sha(project);
            if (!string.Equals(currentSha, originalSha, StringComparison.Ordinal) &&
                !string.Equals(currentSha, proposedSha, StringComparison.Ordinal))
                throw new IOException("Il .javaproj e stato modificato dopo il crash: " +
                    "recupero automatico vietato per non perdere cambiamenti dell'utente.");
            var snapshot = new Pending { Directory = dir, Journal = journal,
                Project = project, OriginalSha = originalSha, ProposedSha = proposedSha,
                State = state };
            snapshot.Jars.AddRange(items);
            return snapshot;
        }

        // The only permitted project change between backup and proposed is
        // one managed JavaJarReference for each journal addition. This prevents
        // recovering from a journal/proposed file that hides unrelated edits.
        private static void VerifyProposedXml(string backup, string proposed, string root,
            IList<NewJar> jars)
        {
            XmlDocument original = ReadXml(backup);
            XmlDocument candidate = ReadXml(proposed);
            foreach (var jar in jars)
            {
                string relative = jar.Path.Substring(root.Length).TrimStart('\\', '/');
                relative = relative.Replace('/', '\\');
                XmlElement[] matches = candidate.DocumentElement.SelectNodes(".//*")
                    .OfType<XmlElement>().Where(x => x.LocalName == "JavaJarReference" &&
                        string.Equals(x.GetAttribute("Include").Replace('/', '\\'), relative,
                            StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1)
                    throw new InvalidDataException("Proposta senza JavaJarReference univoco: " + relative);
                XmlElement item = matches[0];
                string[] parts = relative.Split('\\');
                if (parts.Length != 6 ||
                    !string.Equals(parts[0], "lib", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(parts[1], "java-packages", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Path JAR fuori dalla struttura gestita.");
                string owner = parts[2] + ":" + parts[3];
                string version = parts[4];
                Func<string, string> meta = key => item.ChildNodes.OfType<XmlElement>()
                    .Where(x => x.LocalName == key).Select(x => x.InnerText).FirstOrDefault();
                if (!string.Equals(meta(JavaPlainPackageManager.ManagedId), owner,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(meta(JavaPlainPackageManager.ManagedVersion), version,
                        StringComparison.Ordinal) ||
                    !JavaPackageScopes.IsSupported(meta(JavaPackageScopes.PlainMetadata)))
                    throw new InvalidDataException("Metadata JAR proposta non coerenti.");
                bool ownerPreviouslyInstalled = original.DocumentElement.SelectNodes(".//*")
                    .OfType<XmlElement>().Where(x => x.LocalName == "JavaJarReference")
                    .Any(x => string.Equals(x.ChildNodes.OfType<XmlElement>()
                        .FirstOrDefault(c => c.LocalName == JavaPlainPackageManager.ManagedId)
                        ?.InnerText, owner, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(x.ChildNodes.OfType<XmlElement>()
                        .FirstOrDefault(c => c.LocalName == JavaPlainPackageManager.ManagedVersion)
                        ?.InnerText, version, StringComparison.Ordinal) &&
                        string.Equals(JavaPackageScopes.Normalize(x.ChildNodes.OfType<XmlElement>()
                        .FirstOrDefault(c => c.LocalName == JavaPackageScopes.PlainMetadata)
                        ?.InnerText), JavaPackageScopes.Normalize(meta(JavaPackageScopes.PlainMetadata)),
                            StringComparison.OrdinalIgnoreCase));
                if (!ownerPreviouslyInstalled)
                    throw new InvalidDataException("Proprietario JAR non presente nell'originale.");
                item.ParentNode.RemoveChild(item);
            }
            if (!string.Equals(original.DocumentElement.OuterXml,
                candidate.DocumentElement.OuterXml, StringComparison.Ordinal))
                throw new InvalidDataException("La proposta modifica altro oltre ai JavaJarReference autorizzati.");
        }

        private static XmlDocument ReadXml(string path)
        {
            var settings = new XmlReaderSettings { XmlResolver = null,
                DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 8 * 1024 * 1024 };
            var doc = new XmlDocument { XmlResolver = null, PreserveWhitespace = false };
            using (var stream = new FileStream(Io(path), FileMode.Open,
                FileAccess.Read, FileShare.Read))
            using (var reader = XmlReader.Create(stream, settings)) doc.Load(reader);
            if (doc.DocumentElement == null || doc.DocumentElement.LocalName != "Project")
                throw new InvalidDataException("File progetto senza nodo Project.");
            return doc;
        }

        private static string ValidateProject(string projectFile)
        {
            if (string.IsNullOrWhiteSpace(projectFile))
                throw new ArgumentException("Progetto non selezionato.");
            string project = Path.GetFullPath(projectFile);
            if (!string.Equals(Path.GetExtension(project), ".javaproj",
                StringComparison.OrdinalIgnoreCase) || !File.Exists(Io(project)))
                throw new FileNotFoundException("Progetto Plain non presente.", project);
            EnsureSafe(Path.GetDirectoryName(project), project);
            return project;
        }

        private static void EnsureSafe(string root, string fullPath)
        {
            root = Path.GetFullPath(root).TrimEnd('\\', '/');
            string path = Path.GetFullPath(fullPath);
            if (!Paths.Equals(root, path) && !IsBelow(root, path))
                throw new InvalidDataException("Percorso fuori dal progetto: " + path);
            if ((File.GetAttributes(Io(root)) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Root progetto con junction/symlink.");
            string current = root;
            foreach (string segment in path.Substring(root.Length).Split(
                new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if ((File.Exists(Io(current)) || Directory.Exists(Io(current))) &&
                    (File.GetAttributes(Io(current)) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Junction/symlink non consentito: " + current);
            }
        }

        private static bool IsBelow(string root, string path)
        {
            string prefix = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static string StripExtended(string path)
        {
            return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path.Substring(4) : path;
        }

        // Only file APIs receive extended Windows paths; XML/journal references
        // always retain ordinary MSBuild relative/absolute paths.
        private static string Io(string path)
        {
            string full = Path.GetFullPath(path);
            return Path.DirectorySeparatorChar == '\\' && !full.StartsWith(@"\\?\",
                StringComparison.Ordinal) ? @"\\?\" + full : full;
        }

        private static bool IsSha(string value)
        {
            return value != null && value.Length == 64 &&
                value.All(x => (x >= '0' && x <= '9') || (x >= 'a' && x <= 'f'));
        }

        private static string Sha(string path)
        {
            using (var stream = new FileStream(Io(path), FileMode.Open,
                FileAccess.Read, FileShare.Read))
            using (var digest = SHA256.Create())
                return BitConverter.ToString(digest.ComputeHash(stream)).Replace("-", "")
                    .ToLowerInvariant();
        }

        private static bool EqualHash(string path, string expected)
        {
            return string.Equals(Sha(path), expected, StringComparison.Ordinal);
        }

        private static void DurableWrite(string journal, string content)
        {
            string temp = Path.Combine(Path.GetDirectoryName(journal),
                ".jpm-journal-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                byte[] data = new UTF8Encoding(false).GetBytes(content);
                using (var stream = new FileStream(Io(temp), FileMode.CreateNew,
                    FileAccess.Write, FileShare.None))
                {
                    stream.Write(data, 0, data.Length);
                    stream.Flush(true);
                }
                File.Replace(Io(temp), Io(journal), null, true);
            }
            finally { if (File.Exists(Io(temp))) File.Delete(Io(temp)); }
        }

        private static bool IsExpected(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException ||
                ex is InvalidOperationException || ex is ArgumentException ||
                ex is XmlException || ex is NotSupportedException;
        }
    }
}
