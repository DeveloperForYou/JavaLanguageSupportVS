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
    // JPM-S2D421-REMOVAL-TRANSACTION-20261126
    // Deliberately narrow: remove ONE unused MANAGED TRANSITIVE reference only.
    // NO JAR is physically deleted: the old binary remains available for rollback.
    // Direct dependencies, manually added JARs, multiple owners, version changes,
    // mixed Maven deltas and unverified graphs all fail closed.
    internal static class JavaPlainResolutionRemovalCommit
    {
        internal const string Version = "JPM-S2D.4.2.1";
        private const string RootName = "plain-resolution-transactions";
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        internal sealed class Candidate
        {
            internal string Project;
            internal string Jar;
            internal string Sha256;
            internal string Coordinate;
            internal string Owner;
            internal string OwnerVersion;
            internal string Scope;
            internal string Details;
        }
        internal sealed class Result
        {
            internal string TransactionDirectory;
            internal string Details;
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
            internal string OriginalHash;
            internal string ProposedHash;
            internal string RemovedJar;
            internal string RemovedHash;
            internal string State;
        }

        internal static Candidate Inspect(string input,
            JavaPlainMavenResolutionPlanner.Plan plan,
            JavaPlainResolutionTransactionPreflight.Validation prior,
            JavaPlainResolutionJarStaging.StageResult stage)
        {
            if (plan == null || prior == null || stage == null ||
                plan.ClasspathDiffs == null || stage.Files == null || stage.NothingToStage)
                throw new InvalidOperationException(
                    "Eseguire Analizza con Maven, Valida transazione, Prepara JAR.");
            if (plan.HasOverride || plan.Unverified != 0 || plan.Added != 0 ||
                plan.Updated != 0 || plan.Removed != 1 ||
                plan.ClasspathDiffs.Count(x => x.Status == "Rimosso") != 1)
                throw new NotSupportedException("Rimozione sicura limitata a UN JAR transitivo " +
                    "non piu richiesto da Maven, senza altri cambiamenti o override.");
            // JPM-S2D421-REMOVAL-PREFLIGHT-GATE-20261126
            // S2D.3.1 counts the single REMOVAL as one critical review.
            // Exactly one is required here: >=2 also means other critical
            // problems (conflicts/unverified JARs), which MUST still fail.
            // The strict one-removal/no-additions/no-updates guard above,
            // fingerprint and staging checks below remain unchanged.
            if (prior.BlockingIssueCount != 1 || prior.ChangedCount != 1 ||
                !string.Equals(prior.ManifestFingerprint, stage.SourceFingerprint,
                    StringComparison.Ordinal))
                throw new IOException("Preflight e staging differenti: ricominciare l'analisi.");
            string project = ValidateProject(input);
            string root = Path.GetDirectoryName(project);
            if (!string.Equals(JavaPackageManagerInstalledReader.Read(project).Backend,
                "Plain", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Solo progetti Plain.");
            if (JavaPlainResolutionTransactionPreflight.CaptureFingerprint(
                project, CancellationToken.None) != stage.SourceFingerprint)
                throw new IOException("Progetto o JAR variati dopo lo staging.");

            var diff = plan.ClasspathDiffs.Single(x => x.Status == "Rimosso");
            string[] identity = (diff.Coordinate ?? "").Split(':');
            if (identity.Length != 2 || identity.Any(x => !SafeToken(x)) ||
                !SafeVersion(diff.InstalledVersion) ||
                diff.ProposedVersion != "—")
                throw new InvalidDataException("Identita del JAR da rimuovere ambigua.");
            string[] owners = (diff.RequiredBy ?? "").Split(new[] { " | Maven: " },
                StringSplitOptions.None);
            if (owners.Length != 2 || !owners[0].StartsWith("Plain: ", StringComparison.Ordinal) ||
                owners[1] != "—")
                throw new InvalidDataException("Provenienza Maven non deterministica.");
            string owner = owners[0].Substring("Plain: ".Length);
            string[] ownerId = owner.Split(':');
            if (ownerId.Length != 2 || ownerId.Any(x => !SafeToken(x)) ||
                owner == diff.Coordinate)
                throw new NotSupportedException("Una dipendenza diretta non puo essere rimossa qui.");

            XmlDocument xml = ReadXml(project, true);
            var references = References(xml).ToArray();
            string expected = identity[0] + "." + identity[1] + "-" +
                diff.InstalledVersion + ".jar";
            var matches = references.Where(x =>
                string.Equals(Path.GetFileName(x.GetAttribute("Include")), expected,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Child(x, JavaPlainPackageManager.ManagedId), owner,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
                throw new InvalidDataException("JAR transitivo da rimuovere non univoco.");
            XmlElement item = matches[0];
            string ownerVersion = Child(item, JavaPlainPackageManager.ManagedVersion);
            string scope = JavaPackageScopes.Normalize(
                Child(item, JavaPackageScopes.PlainMetadata));
            if (!SafeVersion(ownerVersion) || !JavaPackageScopes.IsSupported(scope))
                throw new InvalidDataException("Metadata versione/scope non sicuri.");
            string folder = Path.GetFullPath(Path.Combine(root, "lib", "java-packages",
                ownerId[0], ownerId[1], ownerVersion));
            string jar = ResolveJar(root, item.GetAttribute("Include"));
            if (!string.Equals(Path.GetDirectoryName(jar), folder,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(jar), expected,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("JAR non nella directory del proprietario.");
            EnsureSafe(root, jar);
            if (!File.Exists(Io(jar))) throw new FileNotFoundException("JAR assente.", jar);
            // Refuse to remove a direct root: there must be a distinct owner root
            // reference with the same managed ID/version/scope.
            string ownerFilename = ownerId[0] + "." + ownerId[1] + "-" +
                ownerVersion + ".jar";
            if (references.Count(x => x != item &&
                string.Equals(Child(x, JavaPlainPackageManager.ManagedId), owner,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Child(x, JavaPlainPackageManager.ManagedVersion), ownerVersion,
                    StringComparison.Ordinal) &&
                JavaPackageScopes.Normalize(Child(x, JavaPackageScopes.PlainMetadata)) == scope &&
                string.Equals(Path.GetFileName(x.GetAttribute("Include")), ownerFilename,
                    StringComparison.OrdinalIgnoreCase)) != 1)
                throw new NotSupportedException("Radice diretta del proprietario non verificata.");
            // A shared physical JAR must never be unlinked ambiguously.
            if (references.Count(x => string.Equals(
                ResolveJar(root, x.GetAttribute("Include")), jar,
                StringComparison.OrdinalIgnoreCase)) != 1)
                throw new NotSupportedException("JAR referenziato da piu voci: operazione bloccata.");
            if (!string.Equals(diff.Scope, scope, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Scope differente da quello rilevato nel grafo Plain.");
            VerifyStagedAll(stage, plan);
            if (stage.Files.Any(x => string.Equals(x.Coordinate, diff.Coordinate,
                    StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Staging incongruente: la dipendenza figura ancora in Maven.");
            string hash = Sha(jar);
            return new Candidate { Project = project, Jar = jar, Sha256 = hash,
                Coordinate = diff.Coordinate, Owner = owner,
                OwnerVersion = ownerVersion, Scope = scope,
                Details = Version + " | ANTEPRIMA RIMOZIONE (SOLO LETTURA)\n" +
                    "Rimuovere SOLO il riferimento al JAR transitivo: " + expected + "\n" +
                    "Identita: " + diff.Coordinate + ":" + diff.InstalledVersion + "\n" +
                    "Proprietario: " + owner + ":" + ownerVersion + " [" + scope + "]\n" +
                    "SHA-256: " + hash + "\n" +
                    "JAR fisico CONSERVATO (rollback / future pulizie).\n" +
                    "Nessuna dipendenza diretta, JAR manuale o altro riferimento sara rimosso.\n" +
                    "La compatibilita binaria/API non e verificata." };
        }

        internal static async Task<Result> ApplyAsync(string project,
            JavaPlainMavenResolutionPlanner.Plan plan,
            JavaPlainResolutionTransactionPreflight.Validation prior,
            JavaPlainResolutionJarStaging.StageResult stage)
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try { return await Task.Run(() => ApplyLocked(project, plan, prior, stage))
                    .ConfigureAwait(false); }
            finally { Gate.Release(); }
        }

        private static Result ApplyLocked(string file,
            JavaPlainMavenResolutionPlanner.Plan plan,
            JavaPlainResolutionTransactionPreflight.Validation prior,
            JavaPlainResolutionJarStaging.StageResult stage)
        {
            string project = ValidateProject(file);
            using (var mutex = new Mutex(false, MutexName(project)))
            {
                bool acquired;
                try { acquired = mutex.WaitOne(TimeSpan.Zero); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new IOException("Un altro processo modifica il progetto.");
                try { return ApplyInsideMutex(project, plan, prior, stage); }
                finally { mutex.ReleaseMutex(); }
            }
        }

        private static Result ApplyInsideMutex(string project,
            JavaPlainMavenResolutionPlanner.Plan plan,
            JavaPlainResolutionTransactionPreflight.Validation prior,
            JavaPlainResolutionJarStaging.StageResult stage)
        {
            if (JavaPlainResolutionCrashRecovery.Inspect(project).PendingCount != 0)
                throw new IOException("Transazione sospesa: usare Recupera interrotto.");
            Candidate first = Inspect(project, plan, prior, stage);
            var fresh = JavaPlainResolutionTransactionPreflight.PrepareAsync(
                project, plan, CancellationToken.None).GetAwaiter().GetResult();
            if (fresh.BlockingIssueCount != 1 || fresh.ChangedCount != 1 ||
                fresh.ManifestFingerprint != prior.ManifestFingerprint ||
                fresh.ManifestFingerprint != stage.SourceFingerprint)
                throw new IOException("Preflight finale differente: transazione annullata.");
            Candidate second = Inspect(project, plan, fresh, stage);
            if (!string.Equals(first.Jar, second.Jar, StringComparison.OrdinalIgnoreCase) ||
                first.Sha256 != second.Sha256)
                throw new IOException("JAR da rimuovere modificato durante la preparazione.");
            string root = Path.GetDirectoryName(project);
            string txRoot = Path.Combine(root, ".java-vs", RootName);
            EnsureSafe(root, Path.Combine(root, ".java-vs"));
            Directory.CreateDirectory(Io(txRoot));
            EnsureSafe(root, txRoot);
            if (JavaPlainResolutionCrashRecovery.Inspect(project).PendingCount != 0)
                throw new IOException("Nuovo journal rilevato durante la transazione.");
            string directory = Path.Combine(txRoot, "tx-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Io(directory));
            string backup = Path.Combine(directory, "project-original.javaproj");
            string proposed = Path.Combine(directory, "project-proposed.javaproj");
            string journal = Path.Combine(directory, "transaction-state.txt");
            string originalHash = Sha(project);
            CopyDurably(project, backup);
            if (Sha(backup) != originalHash)
                throw new IOException("Backup .javaproj non valido.");
            XmlDocument xml = ReadXml(project, true);
            RemoveOnlyReference(xml, root, first.Jar);
            WriteXml(xml, proposed, HasBom(project));
            string proposedHash = Sha(proposed);
            bool started = false;
            try
            {
                WriteJournal(journal, "PREPARED", project, originalHash, proposedHash,
                    first.Jar, first.Sha256);
                started = true;
                if (Sha(project) != originalHash || Sha(first.Jar) != first.Sha256 ||
                    JavaPlainResolutionTransactionPreflight.CaptureFingerprint(
                        project, CancellationToken.None) != stage.SourceFingerprint)
                    throw new IOException("File modificati prima del commit: stop.");
                string temp = Path.Combine(root, ".jpm-remove-" +
                    Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    CopyDurably(proposed, temp);
                    if (Sha(temp) != proposedHash) throw new IOException("Proposta modificata.");
                    File.Replace(Io(temp), Io(project), null, true);
                }
                finally { if (File.Exists(Io(temp))) File.Delete(Io(temp)); }
                WriteJournal(journal, "PROJECT_COMMITTED", project, originalHash,
                    proposedHash, first.Jar, first.Sha256);
                if (Sha(project) != proposedHash || Sha(first.Jar) != first.Sha256)
                    throw new IOException("Post-commit non verificato.");
            }
            catch (Exception ex)
            {
                if (!started)
                    throw new IOException("Journal non inizializzato: verifica manuale in " +
                        directory, ex);
                try { RecoverVerified(project, directory); }
                catch (Exception rollback)
                {
                    try { WriteJournal(journal, "RECOVERY_REQUIRED", project,
                        originalHash, proposedHash, first.Jar, first.Sha256); }
                    catch (Exception) { }
                    throw new IOException("ROLLBACK INCOMPLETO: mantenere backup/journal " +
                        directory, new AggregateException(ex, rollback));
                }
                throw new IOException("Rimozione riferimento fallita: rollback completato.", ex);
            }
            string sync = "Sincronizzazione JDT LS richiesta.";
            try
            {
                JavaDependencyLanguageSync.RefreshPlainProjectNow(project);
                JavaDependencyLanguageSync.RefreshAll();
            }
            catch (Exception ex)
            {
                sync = "AVVISO JDT LS: " + ex.Message;
                JavaJpmProjectLogger.Warning(project, Version, sync);
            }
            try { WriteJournal(journal, "DONE", project, originalHash,
                proposedHash, first.Jar, first.Sha256); }
            catch (Exception ex)
            {
                JavaJpmProjectLogger.Warning(project, Version,
                    "Progetto aggiornato ma chiusura del journal fallita: " + ex.Message);
            }
            return new Result { TransactionDirectory = directory,
                Details = Version + " | RIMOZIONE RIFERIMENTO COMPLETATA\n" +
                    "Non referenziato: " + Path.GetFileName(first.Jar) + "\n" +
                    "JAR fisico conservato: " + first.Jar + "\n" +
                    "Backup: " + backup + "\n" + sync +
                    "\nAnalizza con Maven: attese zero differenze." };
        }

        internal static RecoveryCheck InspectInterrupted(string project, string directory)
        {
            try
            {
                Snapshot s = LoadAndVerify(project, directory);
                return new RecoveryCheck { CanRecover = true,
                    Details = Version + " | TRANSAZIONE INTERROTTA: " + s.State + "\n" +
                        "Il riferimento sara ripristinato dal backup SHA-256.\n" +
                        "Il JAR fisico verra conservato.\nBackup: " +
                        Path.Combine(directory, "project-original.javaproj") };
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                return new RecoveryCheck { CanRecover = false,
                    Details = "RECUPERO BLOCCATO: " + ex.Message };
            }
        }
        internal static string RecoverInterruptedUnderLock(string project, string directory)
        {
            RecoverVerified(project, directory);
            return Version + " | RECUPERO CONCLUSO: ROLLED_BACK. " +
                "Riferimento ripristinato, JAR conservato.";
        }
        private static void RecoverVerified(string project, string directory)
        {
            Snapshot s = LoadAndVerify(project, directory);
            string root = Path.GetDirectoryName(s.Project);
            string backup = Path.Combine(s.Directory, "project-original.javaproj");
            if (Sha(project) == s.ProposedHash && s.ProposedHash != s.OriginalHash)
            {
                string temp = Path.Combine(root, ".jpm-remove-recovery-" +
                    Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    CopyDurably(backup, temp);
                    if (Sha(temp) != s.OriginalHash)
                        throw new IOException("Backup modificato.");
                    File.Replace(Io(temp), Io(project), null, true);
                }
                finally { if (File.Exists(Io(temp))) File.Delete(Io(temp)); }
            }
            if (Sha(project) != s.OriginalHash || Sha(s.RemovedJar) != s.RemovedHash)
                throw new IOException("Ripristino non verificato.");
            WriteJournal(s.Journal, "ROLLED_BACK", s.Project, s.OriginalHash,
                s.ProposedHash, s.RemovedJar, s.RemovedHash);
        }
        private static Snapshot LoadAndVerify(string input, string transaction)
        {
            string project = ValidateProject(input);
            string root = Path.GetDirectoryName(project);
            string expectedRoot = Path.GetFullPath(Path.Combine(root, ".java-vs", RootName));
            string directory = Path.GetFullPath(transaction);
            string name = Path.GetFileName(directory);
            if (!string.Equals(Path.GetDirectoryName(directory), expectedRoot,
                    StringComparison.OrdinalIgnoreCase) || name.Length != 35 ||
                !name.StartsWith("tx-", StringComparison.Ordinal) ||
                !name.Skip(3).All(Uri.IsHexDigit))
                throw new InvalidDataException("Percorso transaction non valido.");
            EnsureSafe(root, directory);
            string journal = Path.Combine(directory, "transaction-state.txt");
            string backup = Path.Combine(directory, "project-original.javaproj");
            string proposed = Path.Combine(directory, "project-proposed.javaproj");
            foreach (string path in new[] { journal, backup, proposed })
            {
                if (!File.Exists(Io(path)))
                    throw new FileNotFoundException("File recovery mancante", path);
                EnsureSafe(root, path);
            }
            if (new FileInfo(Io(journal)).Length > 8192)
                throw new InvalidDataException("Journal troppo grande.");
            string[] lines = File.ReadAllLines(Io(journal), Encoding.UTF8);
            if (lines.Length != 5 ||
                !lines[0].StartsWith(Version + " | ", StringComparison.Ordinal) ||
                lines[1] != "Progetto: " + project ||
                !lines[2].StartsWith("SHA256 originale: ", StringComparison.Ordinal) ||
                !lines[3].StartsWith("SHA256 proposto: ", StringComparison.Ordinal))
                throw new InvalidDataException("Journal rimozione non riconosciuto.");
            string state = lines[0].Substring((Version + " | ").Length);
            if (state != "PREPARED" && state != "PROJECT_COMMITTED" &&
                state != "RECOVERY_REQUIRED")
                throw new InvalidDataException("Stato non recuperabile: " + state);
            string before = lines[2].Substring("SHA256 originale: ".Length);
            string after = lines[3].Substring("SHA256 proposto: ".Length);
            if (!IsSha(before) || !IsSha(after) || Sha(backup) != before ||
                Sha(proposed) != after)
                throw new IOException("SHA256 backup/proposta non validi.");
            string line = lines[4];
            int mark = line.LastIndexOf(" SHA256 ", StringComparison.Ordinal);
            if (!line.StartsWith("- ", StringComparison.Ordinal) || mark < 3)
                throw new InvalidDataException("Voce JAR nel journal non valida.");
            string jar = line.Substring(2, mark - 2);
            string jarHash = line.Substring(mark + " SHA256 ".Length);
            if (!Path.IsPathRooted(jar) || Path.GetFullPath(jar) != jar ||
                !IsSha(jarHash) || !jar.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                !IsBelow(Path.Combine(root, "lib", "java-packages"), jar))
                throw new InvalidDataException("JAR fuori dalla directory gestita.");
            EnsureSafe(root, jar);
            if (!File.Exists(Io(jar)) || Sha(jar) != jarHash)
                throw new IOException("JAR originario modificato: recovery bloccato.");
            XmlDocument xml = ReadXml(backup, true);
            RemoveOnlyReference(xml, root, jar);
            XmlDocument proposedXml = ReadXml(proposed, true);
            if (!string.Equals(xml.DocumentElement.OuterXml,
                proposedXml.DocumentElement.OuterXml, StringComparison.Ordinal))
                throw new IOException("Proposta con variazioni oltre al riferimento: blocco.");
            string live = Sha(project);
            if (live != before && live != after)
                throw new IOException("Progetto modificato dopo il crash: blocco.");
            return new Snapshot { Project = project, Directory = directory, Journal = journal,
                OriginalHash = before, ProposedHash = after, RemovedJar = jar,
                RemovedHash = jarHash, State = state };
        }

        // Independent, final staging verification: a stale/modified TEMP tree
        // must never authorize removal of a classpath reference.
        private static void VerifyStagedAll(
            JavaPlainResolutionJarStaging.StageResult stage,
            JavaPlainMavenResolutionPlanner.Plan plan)
        {
            if (string.IsNullOrWhiteSpace(stage.StageDirectory) ||
                stage.Files == null || stage.Files.Count != plan.SimulatedJarCount ||
                stage.StagedJarCount != plan.SimulatedJarCount)
                throw new IOException("Manifest staging Maven incompleto.");
            string root = Path.GetFullPath(stage.StageDirectory);
            string folder = Path.GetFileName(root);
            string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\', '/');
            const string prefix = "java-vsix-s2d32-";
            if (!string.Equals(Path.GetDirectoryName(root), temp,
                    StringComparison.OrdinalIgnoreCase) ||
                !folder.StartsWith(prefix, StringComparison.Ordinal) ||
                folder.Length != prefix.Length + 32 ||
                !folder.Substring(prefix.Length).All(Uri.IsHexDigit))
                throw new InvalidDataException("Staging fuori dalla directory TEMP ammessa.");
            string jars = Path.Combine(root, "jars");
            EnsureSafe(root, jars);
            if (!Directory.Exists(Io(jars)) ||
                Directory.GetFiles(Io(jars), "*", SearchOption.TopDirectoryOnly).Length != stage.Files.Count ||
                Directory.GetDirectories(Io(jars), "*", SearchOption.TopDirectoryOnly).Length != 0 ||
                stage.Files.Select(x => x.FileName).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() != stage.Files.Count)
                throw new IOException("Staging con JAR mancanti, duplicati o file inattesi.");
            foreach (var f in stage.Files)
            {
                if (f == null || string.IsNullOrWhiteSpace(f.FileName) ||
                    !string.Equals(Path.GetFileName(f.FileName), f.FileName,
                        StringComparison.Ordinal) || !f.FileName.EndsWith(".jar",
                            StringComparison.OrdinalIgnoreCase) ||
                    f.Size < 1 || !IsSha(f.Sha256))
                    throw new InvalidDataException("Elemento staging non valido.");
                string path = Path.Combine(jars, f.FileName);
                EnsureSafe(root, path);
                if (!File.Exists(Io(path)) || new FileInfo(Io(path)).Length != f.Size ||
                    Sha(path) != f.Sha256)
                    throw new IOException("JAR dello staging alterato: " + f.FileName);
            }
        }

        private static void RemoveOnlyReference(XmlDocument xml, string root, string jar)
        {
            XmlElement[] refs = References(xml).Where(x =>
                string.Equals(ResolveJar(root, x.GetAttribute("Include")), jar,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            if (refs.Length != 1 ||
                string.IsNullOrWhiteSpace(Child(refs[0], JavaPlainPackageManager.ManagedId)) ||
                string.IsNullOrWhiteSpace(Child(refs[0], JavaPlainPackageManager.ManagedVersion)))
                throw new InvalidDataException("Riferimento da rimuovere non univoco o manuale.");
            refs[0].ParentNode.RemoveChild(refs[0]);
        }
        private static IEnumerable<XmlElement> References(XmlDocument xml)
        {
            return xml.DocumentElement.ChildNodes.OfType<XmlElement>()
                .Where(x => x.LocalName == "ItemGroup")
                .SelectMany(x => x.ChildNodes.OfType<XmlElement>())
                .Where(x => x.LocalName == "JavaJarReference");
        }
        private static string Child(XmlElement x, string name)
        {
            var child = x.ChildNodes.OfType<XmlElement>()
                .FirstOrDefault(n => n.LocalName == name);
            return child == null ? null : child.InnerText.Trim();
        }
        private static string ResolveJar(string root, string include)
        {
            if (string.IsNullOrWhiteSpace(include) || Path.IsPathRooted(include) ||
                include.IndexOfAny(new[] { '*', '?', '$', '%', '@', '{', '}' }) >= 0 ||
                !include.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Riferimento JavaJarReference non letterale.");
            string result = Path.GetFullPath(Path.Combine(root, include));
            if (!IsBelow(root, result))
                throw new InvalidDataException("JAR fuori dal progetto.");
            return result;
        }
        private static bool SafeToken(string x)
        {
            return !string.IsNullOrWhiteSpace(x) && x.Length <= 200 &&
                x.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '-');
        }
        private static bool SafeVersion(string x)
        {
            return SafeToken(x) && x.Length <= 120 && x != "—" && !x.Contains("..");
        }
        private static string ValidateProject(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Progetto non selezionato.");
            string project = Path.GetFullPath(path);
            if (Path.GetExtension(project).ToLowerInvariant() != ".javaproj" ||
                !File.Exists(Io(project)))
                throw new FileNotFoundException("Progetto assente", project);
            EnsureSafe(Path.GetDirectoryName(project), project);
            return project;
        }
        private static void EnsureSafe(string root, string path)
        {
            string baseRoot = Path.GetFullPath(root).TrimEnd('\\', '/');
            string full = Path.GetFullPath(path);
            if (!string.Equals(full, baseRoot, StringComparison.OrdinalIgnoreCase) &&
                !IsBelow(baseRoot, full))
                throw new InvalidDataException("Percorso non consentito.");
            if ((File.GetAttributes(Io(baseRoot)) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Root con symlink o junction.");
            string current = baseRoot;
            foreach (string part in full.Substring(baseRoot.Length).Split(
                new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if ((File.Exists(Io(current)) || Directory.Exists(Io(current))) &&
                    (File.GetAttributes(Io(current)) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Symlink/junction: " + current);
            }
        }
        private static bool IsBelow(string root, string path)
        {
            return Path.GetFullPath(path).StartsWith(Path.GetFullPath(root)
                .TrimEnd('\\', '/') + Path.DirectorySeparatorChar,
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
            using (var fs = new FileStream(Io(path), FileMode.Open,
                FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "")
                    .ToLowerInvariant();
        }
        private static bool IsSha(string hash)
        {
            return hash != null && hash.Length == 64 && hash.All(c =>
                (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
        }
        private static bool HasBom(string file)
        {
            using (var fs = new FileStream(Io(file), FileMode.Open,
                FileAccess.Read, FileShare.Read))
                return fs.ReadByte() == 0xef && fs.ReadByte() == 0xbb &&
                    fs.ReadByte() == 0xbf;
        }
        private static XmlDocument ReadXml(string path, bool whitespace)
        {
            var settings = new XmlReaderSettings { XmlResolver = null,
                DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 8 * 1024 * 1024 };
            var xml = new XmlDocument { XmlResolver = null,
                PreserveWhitespace = whitespace };
            using (var fs = new FileStream(Io(path), FileMode.Open,
                FileAccess.Read, FileShare.Read))
            using (var reader = XmlReader.Create(fs, settings)) xml.Load(reader);
            if (xml.DocumentElement == null || xml.DocumentElement.LocalName != "Project")
                throw new InvalidDataException("XML progetto non valido.");
            return xml;
        }
        private static void WriteXml(XmlDocument xml, string path, bool bom)
        {
            var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(bom),
                Indent = false, NewLineHandling = NewLineHandling.None, CloseOutput = false };
            using (var fs = new FileStream(Io(path), FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            {
                using (var writer = XmlWriter.Create(fs, settings))
                { xml.Save(writer); writer.Flush(); }
                fs.Flush(true);
            }
        }
        private static void CopyDurably(string source, string destination)
        {
            using (var a = new FileStream(Io(source), FileMode.Open,
                FileAccess.Read, FileShare.Read))
            using (var b = new FileStream(Io(destination), FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            { a.CopyTo(b); b.Flush(true); }
        }
        private static void WriteJournal(string path, string state, string project,
            string originalHash, string proposedHash, string removedJar, string jarHash)
        {
            string data = new StringBuilder().AppendLine(Version + " | " + state)
                .AppendLine("Progetto: " + project)
                .AppendLine("SHA256 originale: " + originalHash)
                .AppendLine("SHA256 proposto: " + proposedHash)
                .AppendLine("- " + removedJar + " SHA256 " + jarHash).ToString();
            string tmp = Path.Combine(Path.GetDirectoryName(path),
                ".jpm-journal-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(data);
                using (var fs = new FileStream(Io(tmp), FileMode.CreateNew,
                    FileAccess.Write, FileShare.None))
                { fs.Write(bytes, 0, bytes.Length); fs.Flush(true); }
                if (File.Exists(Io(path))) File.Replace(Io(tmp), Io(path), null, true);
                else File.Move(Io(tmp), Io(path));
            }
            finally { if (File.Exists(Io(tmp))) File.Delete(Io(tmp)); }
        }
        private static string MutexName(string project)
        {
            using (var sha = SHA256.Create())
                return @"Local\JavaLanguageSupportVS-JPM-S2D33-" +
                    BitConverter.ToString(sha.ComputeHash(
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
