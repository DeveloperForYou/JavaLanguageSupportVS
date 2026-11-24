using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using JavaLanguageSupport.ProjectSystem.Dependencies;

namespace JavaLanguageSupport.ProjectSystem.PackageManager
{
    // JPM-S1-SCOPES-20261123

    // F4F2112-PLAIN-PACKAGES: isolated Maven resolution, explicit project-owned JARs.
    // The JARs are never loaded or executed by this component.
    internal sealed class JavaPlainPackageChange : IDisposable
    {
        private readonly byte[] before, after;
        private readonly string stage, target;
        private readonly string[] jarNames;
        private bool applied;
        internal string ProjectFile { get; private set; }
        internal string Summary { get; private set; }
        internal string BackupPath { get; private set; }
        internal int JarCount { get { return jarNames.Length; } }

        internal JavaPlainPackageChange(string file, byte[] original, byte[] replacement,
            string staging, string destination, string[] jars, string summary)
        {
            ProjectFile = file; before = original; after = replacement;
            stage = staging; target = destination; jarNames = jars;
            Summary = summary;
        }

        internal void Apply()
        {
            if (applied) throw new InvalidOperationException("Operazione Plain già applicata.");
            if (!File.ReadAllBytes(ProjectFile).SequenceEqual(before))
                throw new IOException("Il .javaproj è cambiato dopo la preparazione: riprovare.");
            if (before.SequenceEqual(after))
                throw new InvalidOperationException("Nessuna modifica alle dipendenze.");
            // PLAIN-JAR-UPDATE-SWAP-20261123-B: transactional folder replacement.
            string preparedDirectory = null;
            string retainedJarDirectory = null;
            bool installedNewDirectory = false;
            bool retainedOldDirectory = false;
            string temporaryFile = null;
            int sharedJarCount = 0;
            try
            {
                if (jarNames.Length != 0)
                {
                    string projectRoot = Path.GetDirectoryName(ProjectFile);
                    EnsureDirectorySafe(projectRoot, target);
                    if (File.Exists(target))
                        throw new IOException("Percorso JAR occupato da un file: " + target);

                    // Copy to a unique sibling on the SAME volume first.
                    string parent = Path.GetDirectoryName(target);
                    Directory.CreateDirectory(parent);
                    // PLAIN-JAR-LONGPATH-20261123-C: stage inside the SAME project
                    // volume, but outside long group/artifact paths. JAR files
                    // with long filenames can otherwise exceed Windows path limits.
                    string stagingParent = Path.Combine(projectRoot, ".java-vs", "pkg-stage");
                    EnsureDirectorySafe(projectRoot, stagingParent);
                    Directory.CreateDirectory(stagingParent);
                    for (int attempt = 0; attempt < 10; attempt++)
                    {
                        string candidate = Path.Combine(stagingParent,
                            "p-" + Guid.NewGuid().ToString("N").Substring(0, 12));
                        if (Directory.Exists(candidate) || File.Exists(candidate)) continue;
                        preparedDirectory = candidate;
                        break;
                    }
                    if (preparedDirectory == null)
                        throw new IOException("Impossibile creare una cartella temporanea Plain univoca.");
                    Directory.CreateDirectory(preparedDirectory);
                    // PLAIN-SHARED-JAR-LINKS-20261123-D: other packages retain
                    // independent .javaproj references and directory entries.
                    // Where identical, NTFS hard links share immutable JAR bytes;
                    // on other volumes/file systems use the original safe copy.
                    var sharedCandidates = JavaPlainSharedJarLinks.ReadCandidates(ProjectFile, target);
                    foreach (string jar in jarNames)
                    {
                        string source = Path.Combine(stage, jar);
                        string destination = Path.Combine(preparedDirectory, jar);
                        ValidateJar(source);
                        if (JavaPlainSharedJarLinks.CopyOrLink(source, destination, sharedCandidates))
                            sharedJarCount++;
                    }

                    if (Directory.Exists(target) &&
                        !PlainUpdateJarTreesIdentical(preparedDirectory, target, jarNames))
                    {
                        // A prior install/update kept this folder. Preserve it
                        // outside lib/ so it cannot be included by JDT LS globs.
                        string backupRoot = Path.Combine(projectRoot, ".java-vs",
                            "plain-package-backups");
                        EnsureDirectorySafe(projectRoot, backupRoot);
                        Directory.CreateDirectory(backupRoot);
                        // Keep the retained destination SHORT as well: the original
                        // version + timestamp + full GUID could exceed MAX_PATH
                        // when a dependency has a very long JAR filename.
                        retainedJarDirectory = Path.Combine(backupRoot,
                            "p-" + DateTime.UtcNow.ToString("yyMMddHHmmss") + "-" +
                            Guid.NewGuid().ToString("N").Substring(0, 12));
                        Directory.Move(target, retainedJarDirectory);
                        retainedOldDirectory = true;
                    }
                    if (!Directory.Exists(target))
                    {
                        Directory.Move(preparedDirectory, target);
                        preparedDirectory = null;
                        installedNewDirectory = true;
                    }
                }
                // Check again after copying: project may be edited asynchronously by CPS.
                if (!File.ReadAllBytes(ProjectFile).SequenceEqual(before))
                    throw new IOException("Il .javaproj è cambiato durante la copia; nessuna modifica confermata.");
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
                BackupPath = ProjectFile + ".java-backup-" + stamp;
                if (File.Exists(BackupPath)) BackupPath += "-" + Guid.NewGuid().ToString("N");
                File.Copy(ProjectFile, BackupPath, false);
                temporaryFile = Path.Combine(Path.GetDirectoryName(ProjectFile),
                    ".plain-package-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (var output = new FileStream(temporaryFile, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None))
                { output.Write(after, 0, after.Length); output.Flush(true); }
                if (!File.ReadAllBytes(ProjectFile).SequenceEqual(before))
                    throw new IOException("Il .javaproj è cambiato prima del commit; riprovare.");
                File.Replace(temporaryFile, ProjectFile, null);
                temporaryFile = null;
                applied = true;
                if (sharedJarCount != 0)
                    Summary += " JAR condivisi tramite NTFS: " + sharedJarCount + ".";
                if (retainedOldDirectory)
                    Summary += " Backup JAR: " + retainedJarDirectory + ".";
                // Old JARs are intentionally retained as a recoverable copy on disk;
                // their references have been removed from the compiler/JDT classpath.
            }
            catch (Exception operationError)
            {
                try
                {
                    if (installedNewDirectory && Directory.Exists(target))
                        Directory.Delete(target, true);
                    if (retainedOldDirectory && Directory.Exists(retainedJarDirectory))
                    {
                        if (File.Exists(target) || Directory.Exists(target))
                            throw new IOException("La destinazione impedisce il ripristino: " + target);
                        Directory.Move(retainedJarDirectory, target);
                    }
                }
                catch (Exception rollbackError) when (rollbackError is IOException ||
                    rollbackError is UnauthorizedAccessException)
                {
                    throw new IOException("Operazione Plain fallita e ripristino dei JAR " +
                        "non completato. Recupero manuale: " + retainedJarDirectory,
                        new AggregateException(operationError, rollbackError));
                }
                throw;
            }
            finally
            {
                if (preparedDirectory != null && Directory.Exists(preparedDirectory))
                {
                    try { Directory.Delete(preparedDirectory, true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                if (temporaryFile != null && File.Exists(temporaryFile)) File.Delete(temporaryFile);
            }
            // Only after a successful commit. Failures here must not roll back
            // the new JAR directory or misreport a successful installation.
            // JPM-S124-CLASSPATH-RECONCILE-20261124:
            // Update the Eclipse model immediately from the committed .javaproj,
            // even before CPS/JDT LS is ready; then notify the ordinary pump.
            try
            {
                JavaDependencyLanguageSync.RefreshPlainProjectNow(ProjectFile);
                JavaDependencyLanguageSync.RefreshAll();
            }
            catch (Exception ex) { Debug.WriteLine("Plain dependency refresh: " + ex); }
        }

        // PLAIN-JAR-UPDATE-SWAP-20261123-B: compare fully downloaded JARs
        // against a previously retained version without relying on other helpers.
        private static bool PlainUpdateJarTreesIdentical(string prepared,
            string existing, string[] names)
        {
            if (Directory.GetDirectories(existing, "*",
                SearchOption.TopDirectoryOnly).Length != 0) return false;
            var actual = Directory.GetFiles(existing, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
            var expected = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
            if (!actual.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase)) return false;
            foreach (string name in names)
            {
                string oldJar = Path.Combine(existing, name);
                string newJar = Path.Combine(prepared, name);
                if ((File.GetAttributes(oldJar) & FileAttributes.ReparsePoint) != 0)
                    return false;
                // Streaming SHA-256: no artificial MB or JAR-count limits.
                if (new FileInfo(oldJar).Length != new FileInfo(newJar).Length)
                    return false;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    byte[] prior, updated;
                    using (var stream = File.OpenRead(oldJar)) prior = sha.ComputeHash(stream);
                    using (var stream = File.OpenRead(newJar)) updated = sha.ComputeHash(stream);
                    if (!prior.SequenceEqual(updated)) return false;
                }
            }
            return true;
        }



        internal static void EnsureDirectorySafe(string projectRoot, string destinationParent)
        {
            string root = Path.GetFullPath(projectRoot).TrimEnd('\\', '/');
            string destination = Path.GetFullPath(destinationParent);
            if (!destination.StartsWith(root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
                throw new IOException("Percorso JAR esterno al progetto.");
            string current = root;
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Root progetto collegata: installazione bloccata.");
            foreach (string part in destination.Substring(root.Length).TrimStart('\\', '/')
                .Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if (Directory.Exists(current) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Directory JAR collegata: installazione bloccata.");
            }
        }

        internal static void ValidateJar(string file)
        {
            if (!file.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
                throw new InvalidDataException("Il resolver non ha restituito un file JAR valido.");
            var info = new FileInfo(file);
            if (info.Length == 0 || info.Length > 100L * 1024 * 1024 ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("JAR vuoto, troppo grande o collegato: " + info.Name);
            using (var fs = File.OpenRead(file))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Read))
                if (archive.Entries.Count == 0 || archive.Entries.Count > 50000)
                    throw new InvalidDataException("Archivio JAR non valido: " + info.Name);
        }

        public void Dispose()
        {
            if (stage == null || !Directory.Exists(stage)) return;
            try { Directory.Delete(stage, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // PLAIN-SHARED-JAR-LINKS-20261123-D: physical sharing without changing
    // the existing per-package ownership metadata or JDT LS classpath model.
    // A link has its own file name/directory entry: removing package A's
    // references/directory cannot invalidate package B's JAR.
    // We never hard-link untrusted paths, and never modify a linked JAR in-place.
    internal static class JavaPlainSharedJarLinks
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll",
            EntryPoint = "CreateHardLinkW", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
            SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(
            System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool CreateHardLink(string newFileName,
            string existingFileName, IntPtr securityAttributes);

        // Only explicit package-manager-owned JARs from the *current* .javaproj
        // are eligible as donors. Old backups/uninstalled package folders are
        // deliberately ignored (they may be stale or otherwise untrusted).
        internal static IDictionary<string, List<string>> ReadCandidates(
            string projectFile, string installingTarget)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            string root = Path.GetDirectoryName(Path.GetFullPath(projectFile));
            string managedRoot = Path.Combine(root, "lib", "java-packages")
                .TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string targetPrefix = Path.GetFullPath(installingTarget)
                .TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var settings = new XmlReaderSettings
            {
                XmlResolver = null, DtdProcessing = DtdProcessing.Prohibit,
                MaxCharactersInDocument = 8 * 1024 * 1024
            };
            var doc = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(projectFile, settings)) doc.Load(reader);
            if (doc.DocumentElement == null || doc.DocumentElement.LocalName != "Project")
                throw new InvalidDataException("Il .javaproj non contiene un elemento Project valido.");
            foreach (var group in doc.DocumentElement.ChildNodes.OfType<XmlElement>()
                .Where(n => n.LocalName == "ItemGroup"))
            foreach (var item in group.ChildNodes.OfType<XmlElement>()
                .Where(n => n.LocalName == "JavaJarReference"))
            {
                if (!item.ChildNodes.OfType<XmlElement>().Any(n =>
                    n.LocalName == "JavaPackageManagerId" &&
                    !string.IsNullOrWhiteSpace(n.InnerText))) continue;
                string include = item.GetAttribute("Include");
                // Never expand MSBuild expressions, wildcards, environment vars,
                // or a path to a file the user did not explicitly reference.
                if (string.IsNullOrWhiteSpace(include) ||
                    include.IndexOfAny(new[] { '$', '%', '*', '?', ';' }) >= 0 ||
                    !include.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;
                string full;
                try
                {
                    full = Path.GetFullPath(Path.IsPathRooted(include) ? include :
                        Path.Combine(root, include));
                    if (!full.StartsWith(managedRoot, StringComparison.OrdinalIgnoreCase) ||
                        full.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase) ||
                        !File.Exists(full) ||
                        (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                        continue;
                    JavaPlainPackageChange.EnsureDirectorySafe(root, Path.GetDirectoryName(full));
                }
                catch (Exception error) when (error is IOException ||
                    error is UnauthorizedAccessException || error is ArgumentException ||
                    error is NotSupportedException) { continue; }
                string fileName = Path.GetFileName(full);
                List<string> names;
                if (!result.TryGetValue(fileName, out names))
                    result[fileName] = names = new List<string>();
                if (!names.Any(p => string.Equals(p, full,
                    StringComparison.OrdinalIgnoreCase))) names.Add(full);
            }
            return result;
        }

        // Returns true ONLY when Windows actually created a hard link.
        // Unsupported file system, permissions, MAX_PATH, or hash mismatch
        // transparently falls back to a byte-for-byte copy into the new staging
        // directory. No external JARs and no JAR count/size limits are used.
        internal static bool CopyOrLink(string downloaded, string destination,
            IDictionary<string, List<string>> candidates)
        {
            List<string> matches;
            if (candidates != null && candidates.TryGetValue(Path.GetFileName(downloaded),
                out matches))
            {
                foreach (string existing in matches)
                {
                    try
                    {
                        if (!SameContent(downloaded, existing)) continue;
                        if (!CreateHardLink(destination, existing, IntPtr.Zero)) continue;
                        // A donor can change between hash check and link creation.
                        // Never accept a link pointing at different bytes.
                        if (SameContent(downloaded, destination)) return true;
                        File.Delete(destination);
                    }
                    catch (Exception error) when (error is IOException ||
                        error is UnauthorizedAccessException ||
                        error is System.Runtime.InteropServices.ExternalException ||
                        error is EntryPointNotFoundException ||
                        error is DllNotFoundException ||
                        error is BadImageFormatException)
                    {
                        // If an attempted link was created but validation failed,
                        // remove only this temporary directory entry.
                        if (File.Exists(destination)) File.Delete(destination);
                    }
                }
            }
            using (var input = File.OpenRead(downloaded))
            using (var output = new FileStream(destination, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(true);
            }
            return false;
        }

        private static bool SameContent(string left, string right)
        {
            if ((File.GetAttributes(right) & FileAttributes.ReparsePoint) != 0 ||
                new FileInfo(left).Length != new FileInfo(right).Length)
                return false;
            using (var digest = System.Security.Cryptography.SHA256.Create())
            {
                byte[] first, second;
                using (var stream = File.OpenRead(left)) first = digest.ComputeHash(stream);
                using (var stream = File.OpenRead(right)) second = digest.ComputeHash(stream);
                return first.SequenceEqual(second);
            }
        }
    }

    internal static class JavaPlainPackageManager
    {
        private static readonly Regex Segment = new Regex(@"^[A-Za-z0-9_][A-Za-z0-9_.-]*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex Version = new Regex(@"^[A-Za-z0-9][A-Za-z0-9_.+\-]*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        internal const string ManagedId = "JavaPackageManagerId";
        internal const string ManagedVersion = "JavaPackageManagerVersion";

        private static void ValidateCoordinate(string group, string artifact, string version)
        {
            if (string.IsNullOrWhiteSpace(group) || string.IsNullOrWhiteSpace(artifact) ||
                group.Length > 200 || artifact.Length > 120 ||
                !group.Split('.').All(x => Segment.IsMatch(x) && x != "." && x != "..") ||
                !Segment.IsMatch(artifact) || artifact == "." || artifact == "..")
                throw new ArgumentException("Coordinate Maven non valide.");
            if (version != null && (!Version.IsMatch(version) || version.Length > 120 ||
                version == "." || version == ".." || version.EndsWith("SNAPSHOT", StringComparison.OrdinalIgnoreCase)))
                throw new NotSupportedException("Versione Plain non supportata (solo release letterali).");
        }

        internal static async Task<JavaPlainPackageChange> PrepareAsync(string projectFile,
            JavaDependencyOperation operation, string group, string artifact, string version,
            IList<JavaPackageSource> sources, CancellationToken token, string scope = "compile")
        {
            string stage = null;
            try
            {
                ValidateCoordinate(group, artifact, operation == JavaDependencyOperation.Remove ? null : version);
                scope = JavaPackageScopes.Require(scope);
                if (string.IsNullOrWhiteSpace(projectFile) || !File.Exists(projectFile))
                    throw new IOException("Progetto .javaproj non disponibile.");
                var snapshot = JavaPackageManagerInstalledReader.Read(projectFile);
                if (!string.Equals(snapshot.Backend, "Plain", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException("Questa operazione è riservata ai progetti Plain.");
                string root = Path.GetDirectoryName(Path.GetFullPath(projectFile));
                if (operation != JavaDependencyOperation.Remove)
                {
                    stage = Path.Combine(Path.GetTempPath(), "java-vsix-plain-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(stage);
                    await ResolveAsync(projectFile, root, group, artifact, version,
                        sources, stage, token, scope).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                return BuildPlan(projectFile, operation, group, artifact, version, stage, scope);
            }
            catch
            {
                if (stage != null && Directory.Exists(stage))
                { try { Directory.Delete(stage, true); } catch (IOException) { } }
                throw;
            }
        }

        // Internal/testable offline plan: stage already contains JARs that Maven resolved.
        internal static JavaPlainPackageChange BuildPlan(string projectFile,
            JavaDependencyOperation operation, string group, string artifact, string version, string stage,
            string scope = "compile")
        {
            ValidateCoordinate(group, artifact, operation == JavaDependencyOperation.Remove ? null : version);
            scope = JavaPackageScopes.Require(scope);
            string root = Path.GetDirectoryName(Path.GetFullPath(projectFile));
            byte[] raw = File.ReadAllBytes(projectFile);
            bool bom = raw.Length >= 3 && raw[0] == 239 && raw[1] == 187 && raw[2] == 191;
            string source = new UTF8Encoding(false, true).GetString(raw, bom ? 3 : 0,
                raw.Length - (bom ? 3 : 0));
            if (source.Contains("\r") && !source.Contains("\r\n") ||
                source.Contains("\r\n") && source.Replace("\r\n", "").Contains("\n"))
                throw new NotSupportedException("Terminatori di riga misti o CR isolato.");
            string nl = source.Contains("\r\n") ? "\r\n" : "\n";
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024 };
            var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
            using (var reader = XmlReader.Create(new StringReader(source), settings)) doc.Load(reader);
            var project = doc.DocumentElement;
            if (project == null || project.LocalName != "Project")
                throw new NotSupportedException("Formato .javaproj non valido.");
            string backend = project.SelectNodes(".//*").OfType<XmlElement>()
                .Where(x => x.LocalName == "JavaBuildSystem").Select(x => x.InnerText.Trim()).FirstOrDefault() ?? "Plain";
            if (!string.Equals(backend, "Plain", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Progetto non Plain.");
            string id = group + ":" + artifact;
            var allJars = project.SelectNodes(".//*").OfType<XmlElement>()
                .Where(x => x.LocalName == "JavaJarReference" && x.ParentNode is XmlElement &&
                    x.ParentNode.LocalName == "ItemGroup" &&
                    x.ParentNode.ParentNode == project).ToList();
            var current = allJars.Where(x => string.Equals(ChildValue(x, ManagedId), id,
                StringComparison.Ordinal)).ToList();
            var currentVersions = current.Select(x => ChildValue(x, ManagedVersion))
                .Distinct(StringComparer.Ordinal).ToList();
            if (currentVersions.Count > 1 || current.Any(x => string.IsNullOrWhiteSpace(ChildValue(x, ManagedVersion))))
                throw new NotSupportedException("Metadati Plain gestiti incoerenti: modifica manuale richiesta.");
            if (operation == JavaDependencyOperation.Install && current.Count != 0)
                throw new InvalidOperationException("Pacchetto già installato: usare Aggiorna.");
            if (operation != JavaDependencyOperation.Install && current.Count == 0)
                throw new InvalidOperationException("Pacchetto Plain gestito non trovato.");
            if (operation == JavaDependencyOperation.Update && currentVersions[0] == version)
                throw new InvalidOperationException("La versione è già installata.");
            if (operation == JavaDependencyOperation.ChangeScope)
            {
                if (!string.Equals(currentVersions[0], version, StringComparison.Ordinal))
                    throw new InvalidOperationException("Per cambiare scope mantenere la versione installata.");
                var oldScopes = current.Select(x => JavaPackageScopes.Normalize(ChildValue(x,
                    JavaPackageScopes.PlainMetadata))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (oldScopes.Count != 1 || !JavaPackageScopes.IsSupported(oldScopes[0]))
                    throw new NotSupportedException("Scope Plain esistente incoerente.");
                if (oldScopes[0] == scope)
                    throw new InvalidOperationException("La dipendenza ha già lo scope selezionato.");
            }
            string[] jars = new string[0];
            string target = null;
            if (operation != JavaDependencyOperation.Remove)
            {
                if (string.IsNullOrWhiteSpace(stage) || !Directory.Exists(stage))
                    throw new IOException("Download JAR non disponibile.");
                jars = Directory.GetFiles(stage, "*.jar", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                if (jars.Length == 0) throw new InvalidDataException("Maven non ha restituito alcun JAR.");
                long size = 0;
                foreach (var jar in jars)
                {
                    JavaPlainPackageChange.ValidateJar(Path.Combine(stage, jar));
                    size += new FileInfo(Path.Combine(stage, jar)).Length;
                    if (size > 500L * 1024 * 1024)
                        throw new InvalidDataException("Dipendenze Plain oltre 500 MB.");
                }
                // Maven copy-dependencies uses [groupId].[artifactId]-[version].jar
                // when -Dmdep.prependGroupId=true (not [groupId]-[artifactId]).
                string primaryJar = group + "." + artifact + "-" + version + ".jar";
                if (!jars.Any(n => string.Equals(n, primaryJar,
                    StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("JAR principale non presente. Atteso: " +
                        primaryJar + "; trovati: " + string.Join(", ", jars) + ".");
                // JPM-S2A-CONFLICT-GUARD-20261124: examine the complete *resulting*
                // Plain managed classpath before touching either JARs or .javaproj.
                JavaPlainVersionConflictGuard.VerifyCandidate(projectFile, id, stage, jars);
                target = Path.Combine(root, "lib", "java-packages", group, artifact, version);
                JavaPlainPackageChange.EnsureDirectorySafe(root, Path.GetDirectoryName(target));
            }
            foreach (var item in current)
            {
                var parent = item.ParentNode;
                parent.RemoveChild(item);
                if (parent.ChildNodes.OfType<XmlElement>().Count() == 0 &&
                    parent is XmlElement && parent.ParentNode == project &&
                    ((XmlElement)parent).GetAttribute("Label") == "JavaPackageManager")
                    project.RemoveChild(parent);
            }
            if (jars.Length != 0)
            {
                var groupNode = doc.CreateElement(project.Prefix, "ItemGroup", project.NamespaceURI);
                groupNode.SetAttribute("Label", "JavaPackageManager");
                foreach (string jar in jars)
                {
                    var item = doc.CreateElement(project.Prefix, "JavaJarReference", project.NamespaceURI);
                    item.SetAttribute("Include", Path.Combine("lib", "java-packages", group, artifact,
                        version, jar).Replace('/', '\\'));
                    var packageId = doc.CreateElement(project.Prefix, ManagedId, project.NamespaceURI);
                    packageId.InnerText = id;
                    var packageVersion = doc.CreateElement(project.Prefix, ManagedVersion, project.NamespaceURI);
                    packageVersion.InnerText = version;
                    item.AppendChild(packageId); item.AppendChild(packageVersion);
                    if (scope != "compile")
                    {
                        var scopeNode = doc.CreateElement(project.Prefix,
                            JavaPackageScopes.PlainMetadata, project.NamespaceURI);
                        scopeNode.InnerText = scope;
                        item.AppendChild(scopeNode);
                    }
                    groupNode.AppendChild(doc.CreateWhitespace(nl + "    "));
                    groupNode.AppendChild(item);
                }
                groupNode.AppendChild(doc.CreateWhitespace(nl + "  "));
                project.AppendChild(doc.CreateWhitespace(nl + "  "));
                project.AppendChild(groupNode);
                project.AppendChild(doc.CreateWhitespace(nl));
            }
            string output = doc.OuterXml.Replace("\r\n", "\n");
            if (nl == "\r\n") output = output.Replace("\n", "\r\n");
            byte[] body = new UTF8Encoding(false).GetBytes(output);
            byte[] bytes = new byte[body.Length + (bom ? 3 : 0)];
            if (bom) { bytes[0] = 239; bytes[1] = 187; bytes[2] = 191; }
            Buffer.BlockCopy(body, 0, bytes, bom ? 3 : 0, body.Length);
            string verb = operation == JavaDependencyOperation.Install ? "Installazione" :
                operation == JavaDependencyOperation.Update ? "Aggiornamento" :
                operation == JavaDependencyOperation.ChangeScope ? "Cambio scope" : "Rimozione";
            return new JavaPlainPackageChange(projectFile, raw, bytes, stage, target, jars,
                verb + " Plain " + id + (operation == JavaDependencyOperation.Remove ? "" : " " + version) +
                "; JAR nel classpath: " + jars.Length + "; scope: " + scope + ".");
        }

        private static string ChildValue(XmlElement node, string name)
        {
            return node.ChildNodes.OfType<XmlElement>()
                .Where(x => x.LocalName == name).Select(x => x.InnerText.Trim()).FirstOrDefault();
        }

        // JPM-LOG-20261124: log the Maven transcript to the owning .javaproj.
        private static async Task ResolveAsync(string projectFile, string projectRoot, string group,
            string artifact, string version, IList<JavaPackageSource> sources, string stage,
            CancellationToken token, string scope)
        {
            var eligible = (sources ?? new List<JavaPackageSource>())
                .Where(JavaPackageMultiRepositorySearchService.IsEligible)
                .GroupBy(x => x.Id, StringComparer.Ordinal).Select(g => g.First()).Take(12).ToList();
            if (eligible.Count == 0)
                throw new InvalidOperationException("Rilevare e selezionare almeno un repository HTTPS attivo.");
            string pom = Path.Combine(stage, "pom.xml");
            string output = Path.Combine(stage, "jars");
            Directory.CreateDirectory(output);
            var xml = new XmlDocument();
            var p = xml.CreateElement("project", "http://maven.apache.org/POM/4.0.0");
            xml.AppendChild(p);
            Add(xml, p, "modelVersion", "4.0.0");
            Add(xml, p, "groupId", "java.vsix.plain");
            Add(xml, p, "artifactId", "temporary-dependency-resolution");
            Add(xml, p, "version", "1.0.0");
            var repos = xml.CreateElement("repositories", p.NamespaceURI); p.AppendChild(repos);
            foreach (var source in eligible)
            {
                var repo = xml.CreateElement("repository", p.NamespaceURI); repos.AppendChild(repo);
                Add(xml, repo, "id", source.Id);
                Add(xml, repo, "url", source.Url.AbsoluteUri);
                var releases = xml.CreateElement("releases", p.NamespaceURI); repo.AppendChild(releases);
                Add(xml, releases, "enabled", source.Releases ? "true" : "false");
                var snapshots = xml.CreateElement("snapshots", p.NamespaceURI); repo.AppendChild(snapshots);
                Add(xml, snapshots, "enabled", source.Snapshots ? "true" : "false");
            }
            var dependencies = xml.CreateElement("dependencies", p.NamespaceURI); p.AppendChild(dependencies);
            var dependency = xml.CreateElement("dependency", p.NamespaceURI); dependencies.AppendChild(dependency);
            Add(xml, dependency, "groupId", group); Add(xml, dependency, "artifactId", artifact);
            Add(xml, dependency, "version", version);
            Add(xml, dependency, "scope", scope);
            using (var writer = XmlWriter.Create(pom,
                new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true })) xml.Save(writer);
            string script = Path.Combine(stage, "resolve.ps1");
            File.WriteAllText(script, MavenRunner, new UTF8Encoding(false));
            JavaJpmProjectLogger.Info(projectFile, "Maven",
                "Risoluzione dipendenze Plain: " + group + ":" + artifact + ":" + version +
                "; scope: " + scope + "; repository HTTPS: " + eligible.Count + ".");
            var start = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\"")
            { WorkingDirectory = projectRoot, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.EnvironmentVariables["JAVA_CPS_PLAIN_ROOT"] = projectRoot;
            start.EnvironmentVariables["JAVA_CPS_PLAIN_POM"] = pom;
            start.EnvironmentVariables["JAVA_CPS_PLAIN_OUT"] = output;
            using (var process = new Process { StartInfo = start })
            {
                if (!process.Start()) throw new IOException("Impossibile avviare Maven.");
                using (token.Register(() => { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } }))
                {
                    Task<string> standard = process.StandardOutput.ReadToEndAsync();
                    Task<string> errors = process.StandardError.ReadToEndAsync();
                    await Task.Run(() => process.WaitForExit(), token).ConfigureAwait(false);
                    string log = await standard.ConfigureAwait(false);
                    string error = await errors.ConfigureAwait(false);
                    JavaJpmProjectLogger.Transcript(projectFile, "Maven", log, error);
                    JavaJpmProjectLogger.Info(projectFile, "Maven", "Resolver terminato: exit code " +
                        process.ExitCode + ".");
                    token.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0)
                        throw new IOException("Risoluzione Maven Plain non riuscita (exit " +
                            process.ExitCode + "): " + Tail(error + "\n" + log, 900));
                }
            }
            var files = Directory.GetFiles(output, "*.jar", SearchOption.TopDirectoryOnly);
            if (files.Length == 0) throw new IOException("Maven non ha prodotto JAR runtime.");
            foreach (var jar in files)
            {
                token.ThrowIfCancellationRequested();
                File.Move(jar, Path.Combine(stage, Path.GetFileName(jar)));
            }
        }

        private static string Tail(string source, int length)
        {
            return source.Length <= length ? source : source.Substring(source.Length - length);
        }
        private static void Add(XmlDocument doc, XmlElement parent, string name, string value)
        {
            var node = doc.CreateElement(name, parent.NamespaceURI);
            node.InnerText = value;
            parent.AppendChild(node);
        }

        private const string MavenRunner = @"
$ErrorActionPreference = 'Stop'
function Resolve-Maven {
    $root = $env:JAVA_CPS_PLAIN_ROOT
    if (Test-Path -LiteralPath (Join-Path $root 'mvnw.cmd') -PathType Leaf) {
        return (Join-Path $root 'mvnw.cmd')
    }
    $found = Get-Command 'mvn.cmd' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { return $found.Source }
    foreach ($homeDir in @($env:MAVEN_HOME, $env:M2_HOME)) {
        if (-not [string]::IsNullOrWhiteSpace($homeDir)) {
            $candidate = Join-Path $homeDir 'bin\mvn.cmd'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
        }
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
    $launcher = Resolve-Maven
    & $launcher '-B' '-ntp' '-f' $env:JAVA_CPS_PLAIN_POM '-N' 'org.apache.maven.plugins:maven-dependency-plugin:3.7.1:copy-dependencies' '-DincludeScope=test' '-DexcludeTypes=pom' ('-DoutputDirectory=' + $env:JAVA_CPS_PLAIN_OUT) '-Dmdep.useRepositoryLayout=false' '-Dmdep.prependGroupId=true'
    if ($LASTEXITCODE -ne 0) { throw ('Maven exit code: ' + $LASTEXITCODE) }
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
";
    }
}
