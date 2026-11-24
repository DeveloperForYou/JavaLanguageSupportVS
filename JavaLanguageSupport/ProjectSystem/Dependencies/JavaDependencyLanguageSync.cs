using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Xml;
using JavaLanguageSupport.LanguageServer;
using Newtonsoft.Json.Linq;

namespace JavaLanguageSupport.ProjectSystem.Dependencies
{
    // CPS owns the evaluated dependencies; JDT LS owns its imported Java model.
    // Do not start another JVM or write Eclipse metadata from the extension.
    internal sealed class JavaDependencyLanguageSync : IDisposable
    {
        private static readonly object Gate = new object();
        private static readonly HashSet<JavaDependencyLanguageSync> Instances =
            new HashSet<JavaDependencyLanguageSync>();
        private static readonly SemaphoreSlim Dispatch = new SemaphoreSlim(1, 1);
        private static object appliedConnection;
        private static string appliedPlainProject;
        private static JObject nativeSettings;
        private static object nativeSettingsConnection;
        private static string initializationRoot;
        private readonly Action refreshTree;
        private string project, backend, requestedJdk, resolvedJdk;
        private string[] jars = new string[0];
        private string[] sourceRoots = new string[0];
        // JPM-S122-CLASSPATH-LIVE-20261124: track the actual file sent to JDT LS,
        // not just whether this call happened to write .classpath itself.
        private string plainProjectStamp;
        // JPM-S123-PLAIN-CLASSPATH-WATCH-20261124: remember direct XML JARs
        // so CPS snapshots cannot resurrect removed manual references.
        private HashSet<string> knownExplicitJars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private FileSystemWatcher plainProjectWatcher;
        private string watchedProject;
        private int plainWatcherRevision;
        private string previousClasspathStamp;
        private object previousClasspathConnection;
        // JPM-S128-JDT-SCOPED-REFRESH-20261124:
        // Eclipse projects also require didChangeConfiguration to activate
        // JDT LS autobuild. This is tracked independently for each LSP session.
        private object scopedEclipseSettingsConnection;
        private Dictionary<string, string> previousJars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private object previousConnection;
        private Dictionary<string, string> previousBuildInputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private object previousBuildConnection;
        private int revision;
        private bool running, disposed;

        internal JavaDependencyLanguageSync(Action refreshTree)
        {
            this.refreshTree = refreshTree;
        }

        internal void Update(string file, string kind, string jdk, string[] references, string[] roots)
        {
            // CPS may re-evaluate when only <JavaPackageManagerScope> changes;
            // that must trigger an editor refresh even if JAR paths are identical.
            string onDiskStamp = Equal(kind, "Plain") ? Stamp(file) : null;
            bool refreshScoped = false;
            lock (Gate)
            {
                if (disposed) return;
                Instances.Add(this);
                if (Equal(project, file) && Equal(backend, kind) && Equal(requestedJdk, jdk) &&
                    jars.SequenceEqual(references, StringComparer.OrdinalIgnoreCase) &&
                    sourceRoots.SequenceEqual(roots, StringComparer.OrdinalIgnoreCase) &&
                    Equal(plainProjectStamp, onDiskStamp)) return;
                if (!Equal(project, file) || !Equal(requestedJdk, jdk)) resolvedJdk = null;
                project = file;
                backend = kind;
                requestedJdk = jdk;
                jars = references;
                sourceRoots = roots;
                plainProjectStamp = onDiskStamp;
                refreshScoped = Equal(kind, "Plain");
                QueueLocked();
            }
            // Eager disk sync also covers references added manually in the
            // .javaproj/CPS while the LSP is busy or not yet initialized.
            if (refreshScoped)
            {
                // Update may receive a stale CPS snapshot just after a JPM
                // File.Replace. Reconcile from the saved .javaproj FIRST.
                RefreshPlainReferencesFromProjectFile(file);
                string[] latest;
                lock (Gate) latest = jars;
                RefreshScopedClasspathOnDisk(file, latest, roots);
            }
            ConfigurePlainProjectWatcher(file, kind);
        }

        internal void JdkResolved(string file, string configuredHome, string home)
        {
            lock (Gate)
            {
                if (disposed || !Equal(project, file) || !Equal(requestedJdk, configuredHome) ||
                    !Equal(backend, "Plain") || Equal(resolvedJdk, home)) return;
                resolvedJdk = home;
                QueueLocked();
            }
        }

        internal void Signal(string file)
        {
            lock (Gate)
                if (!disposed && Equal(project, file)) QueueLocked();
        }

        internal static void ScopeChanged()
        {
            lock (Gate)
                foreach (var instance in Instances) instance.QueueLocked();
        }

        internal static void ServerInitialized()
        {
            lock (Gate)
            {
                if (appliedPlainProject != null) appliedConnection = JavaLspRpc.ConnectionIdentity;
                if (nativeSettings != null) nativeSettingsConnection = JavaLspRpc.ConnectionIdentity;
            }
            ScopeChanged();
        }

        internal static bool HasLoadedProjects => JavaCpsWorkspaceContext.LoadedProjects().Length != 0;

        // Shared entry point for the future package manager. The tree and LS
        // get the same refresh signal; the build tools remain authoritative.
        internal static void RefreshAll()
        {
            JavaDependencyLanguageSync[] selected;
            lock (Gate) selected = Instances.ToArray();
            foreach (var instance in selected)
            {
                string file;
                lock (Gate) file = instance.project;
                if (!JavaCpsWorkspaceContext.ContainsProject(file)) continue;
                // CPS can still expose the *old* ProjectInstance immediately after
                // the package manager atomically replaces the .javaproj.
                // Read explicit on-disk references before notifying JDT LS.
                instance.RefreshPlainReferencesFromProjectFile(file);
                string[] currentJars, currentRoots;
                bool isPlain;
                lock (Gate)
                {
                    isPlain = Equal(instance.backend, "Plain");
                    currentJars = instance.jars;
                    currentRoots = instance.sourceRoots;
                }
                // Do not wait for CPS reevaluation or a JDT LS round trip:
                // .classpath must match the committed JPM operation immediately.
                if (isPlain)
                    RefreshScopedClasspathOnDisk(file, currentJars, currentRoots);
                instance.Signal(file);
                _ = Task.Run(() =>
                {
                    try { instance.refreshTree(); }
                    catch (Exception ex) { Log("Ricarica albero: " + ex.GetType().Name); }
                });
            }
            Log("Ricarica dipendenze richiesta.");
        }

        // JPM-S123: .javaproj can be edited manually or by CPS even when JPM is
        // closed. This watcher only reads that exact file; it never scans folders
        // looking for arbitrary JARs. Debounce both ordinary saves and File.Replace.
        private void ConfigurePlainProjectWatcher(string file, string kind)
        {
            bool required = Equal(kind, "Plain") && !string.IsNullOrWhiteSpace(file);
            lock (Gate)
            {
                if (disposed) return;
                if (required && plainProjectWatcher != null && Equal(watchedProject, file)) return;
                if (!required && plainProjectWatcher == null) return;
            }
            FileSystemWatcher replacement = null;
            if (required)
            {
                try
                {
                    string full = Path.GetFullPath(file);
                    // JPM-S125-MANAGED-CLASSPATH-RECONCILE-20261124:
                    // Also observe edits/replace of managed .classpath by JDT
                    // or other processes. SaveOwned checks byte identity, so
                    // an event caused by our own write is a harmless no-op.
                    // Only files in this project root are observed.
                    replacement = new FileSystemWatcher(Path.GetDirectoryName(full), "*");
                    replacement.NotifyFilter = NotifyFilters.FileName |
                        NotifyFilters.LastWrite | NotifyFilters.Size;
                    replacement.Changed += OnPlainProjectFileChanged;
                    replacement.Created += OnPlainProjectFileChanged;
                    replacement.Renamed += OnPlainProjectFileChanged;
                    replacement.Deleted += OnPlainProjectFileChanged;
                    replacement.Error += (sender, args) =>
                        Log("Plain: watcher .javaproj errore: " + args.GetException()?.Message);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                    ex is ArgumentException || ex is NotSupportedException)
                {
                    replacement?.Dispose();
                    Log("Plain: watcher .javaproj non disponibile: " + ex.Message);
                    return;
                }
            }
            FileSystemWatcher old;
            lock (Gate)
            {
                if (disposed || !Equal(project, file) || !Equal(backend, kind))
                {
                    replacement?.Dispose();
                    return;
                }
                old = plainProjectWatcher;
                plainProjectWatcher = replacement;
                watchedProject = required ? file : null;
                System.Threading.Interlocked.Increment(ref plainWatcherRevision);
            }
            old?.Dispose();
            if (replacement != null)
            {
                try { replacement.EnableRaisingEvents = true; }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                    ex is ArgumentException)
                { Log("Plain: watcher .javaproj non avviato: " + ex.Message); }
            }
        }

        private void OnPlainProjectFileChanged(object sender, FileSystemEventArgs e)
        {
            string file;
            lock (Gate) file = watchedProject;
            if (string.IsNullOrWhiteSpace(file)) return;
            // JPM-S125: reconcile external rewrites as well as deletions.
            // This watcher is nonrecursive and debounced; SaveOwned is a no-op
            // for equal content, preventing an infinite change event loop.
            string changed = Path.GetFileName(e.FullPath);
            bool projectSaved = Equal(changed, Path.GetFileName(file));
            bool classpathChanged = Equal(changed, ".classpath") &&
                (e.ChangeType == WatcherChangeTypes.Changed ||
                 e.ChangeType == WatcherChangeTypes.Created ||
                 e.ChangeType == WatcherChangeTypes.Deleted ||
                 e.ChangeType == WatcherChangeTypes.Renamed);
            bool projectDeleted = Equal(changed, ".project") &&
                e.ChangeType == WatcherChangeTypes.Deleted;
            if (!projectSaved && !classpathChanged && !projectDeleted) return;
            int generation = System.Threading.Interlocked.Increment(ref plainWatcherRevision);
            _ = Task.Run(async () =>
            {
                await Task.Delay(450).ConfigureAwait(false);
                if (generation != System.Threading.Volatile.Read(ref plainWatcherRevision)) return;
                try
                {
                    if (!File.Exists(file)) return;
                    RefreshPlainReferencesFromProjectFile(file);
                    string[] currentJars, currentRoots;
                    lock (Gate)
                    {
                        if (disposed || !Equal(project, file) || !Equal(backend, "Plain") ||
                            generation != plainWatcherRevision) return;
                        currentJars = jars;
                        currentRoots = sourceRoots;
                    }
                    RefreshScopedClasspathOnDisk(file, currentJars, currentRoots);
                    Signal(file); // also refresh JDT LS even when only the scope changed
                }
                catch (Exception ex)
                {
                    Log("Plain: sincronizzazione dopo salvataggio .javaproj: " +
                        ex.GetType().Name + ": " + ex.Message);
                }
            });
        }

        private static void RefreshScopedClasspathOnDisk(string file, string[] references,
            string[] roots)
        {
            try
            {
                string[] effectiveRoots = roots;
                if (effectiveRoots == null || effectiveRoots.Length == 0)
                {
                    string root = Path.GetDirectoryName(Path.GetFullPath(file));
                    var discovered = JavaLanguageSupport.ProjectSystem.Packages.JavaSourceRootResolver
                        .Discover(root, "Plain").Where(x => x.Exists).ToList();
                    var nested = discovered.Where(x => !Equal(x.RelativePath, "src")).ToList();
                    var selected = nested.Count > 0 ? nested : discovered;
                    effectiveRoots = selected.Select(x => x.RelativePath.Replace('\\', '/'))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                }
                var result = JavaScopedClasspathBridge.Synchronize(file, references, effectiveRoots);
                if (result.Changed) Log("Plain: .classpath sincronizzato con .javaproj/CPS.");
                else if (!result.Active && !string.IsNullOrWhiteSpace(result.Note))
                    Log(result.Note);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                ex is ArgumentException || ex is NotSupportedException || ex is XmlException)
            {
                Log("Plain: sincronizzazione anticipata .classpath fallita: " + ex.Message);
            }
        }

        // JPM-S124: explicit post-commit sync, independent of CPS reevaluation,
        // JDT LS connection state, and whether the Dependencies tree was opened.
        // Only the project passed by the JPM is touched; never scans JAR folders.
        internal static void RefreshPlainProjectNow(string file)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(file) ||
                    !string.Equals(Path.GetExtension(file), ".javaproj",
                        StringComparison.OrdinalIgnoreCase) || !File.Exists(file)) return;
                file = Path.GetFullPath(file);
                JavaDependencyLanguageSync current;
                lock (Gate) current = Instances.FirstOrDefault(x => !x.disposed &&
                    Equal(x.project, file) && Equal(x.backend, "Plain"));
                if (current != null)
                {
                    current.RefreshPlainReferencesFromProjectFile(file);
                    string[] existingJars, roots;
                    lock (Gate)
                    {
                        existingJars = current.jars.ToArray();
                        roots = current.sourceRoots.ToArray();
                    }
                    RefreshScopedClasspathOnDisk(file, existingJars, roots);
                    current.Signal(file);
                    return;
                }
                // CPS has not produced a snapshot yet (or its provider was
                // never materialized). The saved project XML remains the owner
                // of direct JAR metadata; imported MSBuild refs will arrive on
                // the next regular CPS Update.
                string root = Path.GetDirectoryName(file);
                var discovered = JavaLanguageSupport.ProjectSystem.Packages.JavaSourceRootResolver
                    .Discover(root, "Plain").Where(x => x.Exists).ToList();
                var nested = discovered.Where(x => !Equal(x.RelativePath, "src")).ToList();
                var selected = nested.Count > 0 ? nested : discovered;
                string[] fallbackRoots = selected.Select(x => x.RelativePath.Replace('\\', '/'))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                RefreshScopedClasspathOnDisk(file, new string[0], fallbackRoots);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                ex is ArgumentException || ex is NotSupportedException || ex is XmlException)
            {
                Log("Plain: refresh esplicito dopo JPM fallito: " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        // RefreshAll is called after the Plain package transaction has committed.
        // CPS reevaluation may lag behind File.Replace: refresh the authoritative
        // literal JavaJarReference items from the current .javaproj on disk.
        // Keep references from imported MSBuild files; only replace the managed
        // lib\java-packages subtree. CPS will subsequently reconcile its snapshot.
        private void RefreshPlainReferencesFromProjectFile(string file)
        {
            lock (Gate)
                if (disposed || !Equal(project, file) || !Equal(backend, "Plain")) return;
            try
            {
                var readerSettings = new XmlReaderSettings
                {
                    XmlResolver = null, DtdProcessing = DtdProcessing.Prohibit,
                    MaxCharactersInDocument = 8 * 1024 * 1024
                };
                XDocument document;
                using (var reader = XmlReader.Create(file, readerSettings))
                    document = XDocument.Load(reader);
                if (document.Root == null || document.Root.Name.LocalName != "Project") return;
                string root = Path.GetDirectoryName(Path.GetFullPath(file));
                var declared = new List<string>();
                foreach (var itemGroup in document.Root.Elements()
                    .Where(x => x.Name.LocalName == "ItemGroup"))
                foreach (var item in itemGroup.Elements()
                    .Where(x => x.Name.LocalName == "JavaJarReference"))
                {
                    string include = (string)item.Attribute("Include");
                    // Never interpret MSBuild expressions or globs as file paths.
                    if (string.IsNullOrWhiteSpace(include) ||
                        include.IndexOfAny(new[] { '*', '?', '$', '%', ';' }) >= 0 ||
                        !include.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        declared.Add(Path.GetFullPath(Path.IsPathRooted(include) ? include :
                            Path.Combine(root, include)));
                    }
                    catch (Exception ex) when (ex is ArgumentException ||
                        ex is NotSupportedException || ex is PathTooLongException) { }
                }
                string managedRoot = Path.Combine(root, "lib", "java-packages")
                    .TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                lock (Gate)
                {
                    if (disposed || !Equal(project, file) || !Equal(backend, "Plain")) return;
                    var explicitNow = new HashSet<string>(declared, StringComparer.OrdinalIgnoreCase);
                    // Remove *previously explicit* references before adding the
                    // current XML declarations. This also covers manual JARs
                    // outside lib/java-packages and preserves imported MSBuild JARs.
                    string[] updated = jars.Where(p => !p.StartsWith(managedRoot,
                            StringComparison.OrdinalIgnoreCase) &&
                            !knownExplicitJars.Contains(p))
                        .Concat(declared).Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
                    knownExplicitJars = explicitNow;
                    if (jars.SequenceEqual(updated, StringComparer.OrdinalIgnoreCase)) return;
                    jars = updated;
                    QueueLocked();
                }
                Log("Plain: JavaJarReference aggiornati dal .javaproj salvato.");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                ex is ArgumentException || ex is XmlException)
            {
                Log("Plain: lettura JAR dal progetto non riuscita: " + ex.GetType().Name);
            }
        }

        private void QueueLocked()
        {
            if (disposed || string.IsNullOrEmpty(project)) return;
            revision++;
            if (running) return;
            running = true;
            _ = Task.Run(PumpAsync);
        }

        private async Task PumpAsync()
        {
            while (true)
            {
                int version;
                string file;
                lock (Gate)
                {
                    if (disposed) { running = false; return; }
                    version = revision;
                    file = project;
                }
                await Task.Delay(1100).ConfigureAwait(false);
                try
                {
                    // Catalog evaluation can precede IVsSolution registration.
                    for (int i = 0; i < 360; i++)
                    {
                        lock (Gate) if (disposed || version != revision) break;
                        if (JavaCpsWorkspaceContext.ContainsProject(file) &&
                            JavaLanguageStatus.IsInitialized && JavaLspRpc.Connected) break;
                        await Task.Delay(250).ConfigureAwait(false);
                    }
                    if (JavaLspRpc.Connected && JavaLanguageStatus.IsInitialized)
                        await JavaWorkspaceActivity.WaitIfBusyAsync(15000, CancellationToken.None).ConfigureAwait(false);
                    await Dispatch.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        bool current;
                        lock (Gate) current = !disposed && version == revision;
                        if (current && JavaCpsWorkspaceContext.ContainsProject(file) &&
                            JavaLanguageStatus.IsInitialized && JavaLspRpc.Connected)
                            await SynchronizeAsync(file, version).ConfigureAwait(false);
                    }
                    finally { Dispatch.Release(); }
                }
                catch (Exception ex) { Log("Sincronizzazione non riuscita: " + ex.GetType().Name + ": " + ex.Message); }
                lock (Gate)
                {
                    if (disposed || version == revision) { running = false; return; }
                }
            }
        }

        private async Task SynchronizeAsync(string file, int version)
        {
            string kind, home;
            string[] references;
            string[] scopedRoots;
            Dictionary<string, string> oldReferences;
            var connection = JavaLspRpc.ConnectionIdentity;
            lock (Gate)
            {
                if (disposed || revision != version || connection == null) return;
                kind = backend;
                home = resolvedJdk;
                references = jars;
                scopedRoots = sourceRoots;
                oldReferences = ReferenceEquals(connection, previousConnection) ? previousJars : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            string root = Path.GetDirectoryName(file);
            if (!IsCurrent(file, version, connection)) return;

            // F4.E.8: in a CPS solution the Java editor may initialize before
            // the project registration event. Add the sole Plain workspace root
            // only when LSP initialize did not provide one. No restart/JVM spawn.
            if (Equal(kind, "Plain"))
                await EnsurePlainWorkspaceAsync(file, connection).ConfigureAwait(false);
            if (!IsCurrent(file, version, connection)) return;
            // JPM-S1.2: unmanaged JDT settings cannot model per-source-root scopes.
            // Generate an Eclipse classpath only for a solitary Plain project
            // that needs it. Existing user-owned .project/.classpath are untouched.
            JavaScopedClasspathBridge.Result scoped = null;
            if (Equal(kind, "Plain"))
            {
                scoped = JavaScopedClasspathBridge.Synchronize(file, references, scopedRoots);
                if (!string.IsNullOrWhiteSpace(scoped.Note)) Log(scoped.Note);
            }
            if (!IsCurrent(file, version, connection)) return;
            // Reconcile global unmanaged-project settings only for the sole CPS
            // root. Absolute paths must never bleed into a different project.
            await SendPlainSettingsAsync(connection).ConfigureAwait(false);
            if (!IsCurrent(file, version, connection)) return;
            // Imported Eclipse projects do not use global unmanaged project
            // settings; SendPlainSettingsAsync returns early in that case.
            // Ensure that the Eclipse model actually enables autobuild. This
            // is necessary for errors to disappear after classpath changes.
            if (scoped != null && scoped.Active &&
                !await SendScopedEclipseAutobuildAsync(connection).ConfigureAwait(false))
            {
                Log("Plain: invio configurazione autobuild Eclipse non riuscito; riprovero' al prossimo refresh.");
                return;
            }
            if (!IsCurrent(file, version, connection)) return;
            // JPM-S1.2-JDTLS-SCOPED-ECLIPSE: imported Eclipse projects use the
            // source/test library flags in .classpath, not a shared unmanaged
            // referencedLibraries list. Keep the existing Maven/Gradle flows.
            if (scoped != null && scoped.Active)
            {
                // An earlier eager sync may already have rewritten .classpath.
                // Compare with the last stamp ACKNOWLEDGED for this LSP connection.
                string classpathFile = Path.Combine(root, ".classpath");
                // The eager/watcher bridge can have written .classpath before
                // this LSP pass. Timestamps plus lengths can miss quick edits.
                // Compare the exact file contents with the last successfully
                // dispatched content on this LSP connection.
                string currentClasspathStamp = StableClasspathSignature(classpathFile);
                bool notify;
                lock (Gate)
                    notify = scoped.Changed ||
                        !ReferenceEquals(previousClasspathConnection, connection) ||
                        !Equal(previousClasspathStamp, currentClasspathStamp);
                if (notify)
                {
                    var scopedChanges = new[]
                    {
                        new { uri = FileUri(Path.Combine(root, ".classpath")), type = scoped.CreatedClasspath ? 1 : 2 },
                        new { uri = FileUri(Path.Combine(root, ".project")), type = scoped.CreatedProject ? 1 : 2 }
                    };
                    bool didNotify = await JavaLspRpc.NotifyForConnectionAsync(connection,
                        "workspace/didChangeWatchedFiles", new { changes = scopedChanges }).ConfigureAwait(false);
                    if (!didNotify)
                    {
                        Log("Plain: didChangeWatchedFiles(.classpath) NON inviato; fingerprint non confermato.");
                        return;
                    }
                    if (!IsCurrent(file, version, connection)) return;
                    string scopedRootUri = FileUri(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar);
                    // The previous request contained only the update list.
                    // When .project had been created by an *eager* disk sync,
                    // JDT LS might never have imported that Eclipse project.
                    // On first use of this connection send both import and
                    // update; afterwards only update. JDT LS schedules these
                    // operations asynchronously in its project manager.
                    bool ensureImport = scoped.CreatedProject ||
                        !ReferenceEquals(previousClasspathConnection, connection);
                    using (var scopedTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
                        await JavaLspRpc.CommandForConnectionAsync(connection,
                            "java.project.changeImportedProjects",
                            new object[] {
                                ensureImport ? new[] { scopedRootUri } : new string[0],
                                new[] { scopedRootUri }, new string[0] },
                            scopedTimeout.Token).ConfigureAwait(false);
                    // JDT LS's changeImportedProjects command returns a void
                    // reply as soon as a WorkspaceJob is QUEUED, not when an
                    // Eclipse project is imported. Check the actual Java model.
                    bool modelReady = await WaitForJdtEclipseProjectAsync(
                        scopedRootUri, file, version, connection).ConfigureAwait(false);
                    if (!modelReady && ensureImport && IsCurrent(file, version, connection))
                    {
                        Log("JDT LS Eclipse: progetto assente dopo changeImportedProjects; richiesta java.project.import.");
                        using (var importTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                            await JavaLspRpc.CommandForConnectionAsync(connection,
                                "java.project.import", new object[0], importTimeout.Token).ConfigureAwait(false);
                        modelReady = await WaitForJdtEclipseProjectAsync(
                            scopedRootUri, file, version, connection).ConfigureAwait(false);
                    }
                    Log("JDT LS Eclipse: progetto importato=" + modelReady +
                        "; import richiesto=" + ensureImport +
                        "; fingerprint=" + (currentClasspathStamp ?? "assente") + ".");
                    if (!modelReady) return; // Do not ACK a classpath JDT hasn't seen.
                    if (!IsCurrent(file, version, connection)) return;
                    // JPM-S131-JDT-GENERIC-CLEANUP-20261124:
                    // Notify JDT LS using the imported Eclipse project root.
                    // Keep this notification: it refreshes the real Java model.
                    bool updated = await JavaLspRpc.NotifyForConnectionAsync(connection,
                        "java/projectConfigurationUpdate",
                        new { uri = scopedRootUri }).ConfigureAwait(false);
                    Log("JDT Eclipse: richiesta aggiornamento root progetto=" + updated + ".");

                    if (!string.IsNullOrWhiteSpace(home) && IsCurrent(file, version, connection))
                    {
                        using (var scopedJdkTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
                            await JavaLspRpc.CommandForConnectionAsync(connection,
                                "java.project.updateJdk", new object[] { scopedRootUri, home },
                                scopedJdkTimeout.Token).ConfigureAwait(false);
                    }
                }
                lock (Gate)
                {
                    previousConnection = connection;
                    previousJars = references.ToDictionary(p => p, Stamp, StringComparer.OrdinalIgnoreCase);
                    if (notify)
                    {
                        previousClasspathConnection = connection;
                        previousClasspathStamp = currentClasspathStamp;
                    }
                }
                Log("Plain: JDT LS scoped Eclipse classpath richiesto; JAR=" + references.Length +
                    "; notifica=" + notify + ".");
                return;
            }
            if (Equal(kind, "Maven") || Equal(kind, "Gradle"))
            {
                string[] names = Equal(kind, "Maven") ? new[] { "pom.xml" } :
                    new[] { "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts" };
                var inputs = names.Concat(Equal(kind, "Gradle") ? new[] { "gradle.properties", Path.Combine("gradle", "libs.versions.toml") } : new string[0])
                    .Select(n => Path.Combine(root, n)).ToDictionary(p => p, Stamp, StringComparer.OrdinalIgnoreCase);
                Dictionary<string, string> oldInputs;
                lock (Gate) oldInputs = ReferenceEquals(previousBuildConnection, connection) ? previousBuildInputs : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var events = new List<object>();
                foreach (var entry in inputs)
                {
                    string old;
                    bool known = oldInputs.TryGetValue(entry.Key, out old);
                    if (entry.Value == old) continue;
                    events.Add(new { uri = FileUri(entry.Key), type = entry.Value == null ? 3 : known && old == null ? 1 : 2 });
                }
                if (events.Count != 0 && IsCurrent(file, version, connection))
                    await JavaLspRpc.NotifyForConnectionAsync(connection, "workspace/didChangeWatchedFiles", new { changes = events }).ConfigureAwait(false);
                lock (Gate) { previousBuildConnection = connection; previousBuildInputs = inputs; }
                if (!IsCurrent(file, version, connection)) return;
                string descriptor = names.Select(n => Path.Combine(root, n)).FirstOrDefault(File.Exists);
                if (descriptor != null && await JavaLspRpc.NotifyForConnectionAsync(connection,
                    "java/projectConfigurationUpdate", new { uri = FileUri(descriptor) }).ConfigureAwait(false))
                    Log(kind + ": aggiornamento configurazione richiesto: " + Path.GetFileName(file));
                // Maven/Gradle choose their own toolchains, never override their JRE container.
                return;
            }

            if (!Equal(kind, "Plain")) return;
            if (!CanManagePlain(file))
            {
                Log("Plain: classpath non modificato; " + DescribePlainScope(file));
                return;
            }

            // Include missing references in settings; notify real disk changes
            // and removals of previously referenced paths without scanning arbitrary JARs.
            var nextReferences = references.ToDictionary(p => p, Stamp, StringComparer.OrdinalIgnoreCase);
            var changes = new List<object>();
            foreach (var entry in nextReferences)
            {
                string old;
                bool known = oldReferences.TryGetValue(entry.Key, out old);
                if (entry.Value != null)
                    changes.Add(new { uri = FileUri(entry.Key), type = known && old == null ? 1 : 2 });
                else if (old != null)
                    changes.Add(new { uri = FileUri(entry.Key), type = 3 });
            }
            foreach (var entry in oldReferences)
                if (entry.Value != null && !nextReferences.ContainsKey(entry.Key) && !File.Exists(entry.Key))
                    changes.Add(new { uri = FileUri(entry.Key), type = 3 });
            if (changes.Count != 0 && IsCurrent(file, version, connection))
                await JavaLspRpc.NotifyForConnectionAsync(connection, "workspace/didChangeWatchedFiles", new { changes }).ConfigureAwait(false);
            if (!IsCurrent(file, version, connection)) return;
            string rootUri = FileUri(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar);
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
            {
                await JavaLspRpc.CommandForConnectionAsync(connection, "java.project.changeImportedProjects",
                    new object[] { new string[0], new[] { rootUri }, new string[0] }, timeout.Token).ConfigureAwait(false);
            }
            if (!string.IsNullOrWhiteSpace(home) && IsCurrent(file, version, connection))
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
                {
                    JToken result = await JavaLspRpc.CommandForConnectionAsync(connection, "java.project.updateJdk",
                        new object[] { rootUri, home }, timeout.Token).ConfigureAwait(false);
                    var response = result as JObject;
                    Log(response != null && (bool?)response["success"] == true ? "Plain: JDK aggiornato." :
                        "Plain: JDK non confermato da JDT LS.");
                }
            }
            lock (Gate)
            {
                previousConnection = connection;
                previousJars = nextReferences;
            }
            Log("Plain: classpath e source roots richiesti; riferimenti=" + references.Length +
                "; source roots=" + string.Join(", ", PlainSourceRoots(file).Values<string>()) +
                ". Indicizzazione asincrona.");
        }

        // Check exact file content, not its timestamp. The .classpath may have
        // been rewritten by JPM and then JDT LS within the same filesystem tick.
        private static string StableClasspathSignature(string file)
        {
            if (!File.Exists(file)) return null;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        // JDT LS can start with autobuild disabled until an explicit
        // workspace/didChangeConfiguration arrives (despite initialize settings).
        // Preserve native VS settings and send only once per LSP connection.
        private async Task<bool> SendScopedEclipseAutobuildAsync(object connection)
        {
            lock (Gate)
                if (ReferenceEquals(scopedEclipseSettingsConnection, connection)) return true;
            JObject settings = JObject.FromObject(JavaLanguageClient.DefaultInitializationOptions)
                ["settings"] as JObject;
            if (settings == null) return false;
            lock (Gate)
            {
                if (ReferenceEquals(nativeSettingsConnection, connection) && nativeSettings != null)
                    settings.Merge(nativeSettings, new JsonMergeSettings
                    {
                        MergeArrayHandling = MergeArrayHandling.Replace
                    });
            }
            JObject javaSettings = settings["java"] as JObject;
            if (javaSettings == null) settings["java"] = javaSettings = new JObject();
            JObject autobuild = javaSettings["autobuild"] as JObject;
            if (autobuild == null) javaSettings["autobuild"] = autobuild = new JObject();
            autobuild["enabled"] = true;
            bool sent = await JavaLspRpc.NotifyForConnectionAsync(connection,
                "workspace/didChangeConfiguration", new { settings }).ConfigureAwait(false);
            if (sent)
            {
                lock (Gate) scopedEclipseSettingsConnection = connection;
                Log("Plain: configurazione autobuild Eclipse inviata a JDT LS.");
            }
            return sent;
        }

        // JDT LS returns URIs of the Java projects actually imported. The
        // update/import request itself is asynchronous: a void RPC response
        // does not mean project model changes (or diagnostics) are complete.
        private async Task<bool> WaitForJdtEclipseProjectAsync(
            string rootUri, string file, int version, object connection)
        {
            Uri target;
            if (!Uri.TryCreate(rootUri, UriKind.Absolute, out target) || !target.IsFile) return false;
            string targetPath = Path.GetFullPath(target.LocalPath).TrimEnd('\\', '/');
            for (int attempt = 0; attempt < 15; attempt++)
            {
                if (!IsCurrent(file, version, connection)) return false;
                try
                {
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
                    {
                        JToken token = await JavaLspRpc.CommandForConnectionAsync(connection,
                            "java.project.getAll", new object[0], timeout.Token).ConfigureAwait(false);
                        var projects = token as JArray;
                        if (projects != null)
                        {
                            foreach (JToken item in projects)
                            {
                                Uri found;
                                if (Uri.TryCreate((string)item, UriKind.Absolute, out found) && found.IsFile &&
                                    Equal(Path.GetFullPath(found.LocalPath).TrimEnd('\\', '/'), targetPath))
                                    return true;
                            }
                            if (attempt == 0)
                                Log("JDT LS Eclipse: progetto Plain non presente nel modello; progetti Java=" +
                                    projects.Count + "; root=" + targetPath);
                        }
                        else
                        {
                            Log("JDT LS Eclipse: java.project.getAll non ha restituito un elenco; impossibile confermare.");
                            return false;
                        }
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException || ex is IOException ||
                    ex is InvalidOperationException || ex is ArgumentException)
                {
                    Log("JDT LS Eclipse: interrogazione modello fallita: " + ex.GetType().Name +
                        ": " + ex.Message);
                    return false;
                }
                await Task.Delay(650).ConfigureAwait(false);
            }
            return false;
        }

        private bool IsCurrent(string file, int version, object connection)
        {
            lock (Gate)
                if (disposed || revision != version || !Equal(project, file)) return false;
            return ReferenceEquals(connection, JavaLspRpc.ConnectionIdentity) &&
                JavaLanguageStatus.IsInitialized && JavaCpsWorkspaceContext.ContainsProject(file);
        }

        // F4.E.8: source paths belong to the real JDT workspace root. It may
        // contain a single CPS project in a subfolder (e.g. solution directory).
        // Never apply global unmanaged-project settings across unrelated roots.
        private static bool IsInWorkspace(string workspaceRoot, string directory)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot) ||
                string.IsNullOrWhiteSpace(directory)) return false;
            string normalized = workspaceRoot.TrimEnd('\\', '/');
            return Equal(normalized, directory) || directory.StartsWith(
                normalized + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool CanManagePlain(string file)
        {
            var loaded = JavaCpsWorkspaceContext.LoadedProjects();
            if (loaded.Length != 1 || !Equal(loaded[0], file)) return false;
            string directory = Path.GetDirectoryName(file);
            if (File.Exists(Path.Combine(directory, ".project"))) return false;
            string root;
            lock (Gate) root = initializationRoot;
            // When initialize arrived before the CPS project was registered,
            // EnsurePlainWorkspaceAsync announces this root before settings.
            return root == null ? Equal(JavaCpsWorkspaceContext.ProjectRoot, directory) :
                IsInWorkspace(root, directory);
        }

        private static string DescribePlainScope(string file)
        {
            var loaded = JavaCpsWorkspaceContext.LoadedProjects();
            string directory = Path.GetDirectoryName(file);
            string root;
            lock (Gate) root = initializationRoot;
            return "progetti CPS=" + loaded.Length +
                "; progetto registrato=" + loaded.Any(p => Equal(p, file)) +
                "; root iniziale presente=" + !string.IsNullOrWhiteSpace(root) +
                "; root contiene progetto=" + IsInWorkspace(root, directory) +
                "; root CPS corrente coincide=" + Equal(JavaCpsWorkspaceContext.ProjectRoot, directory) +
                "; metadati Eclipse=" + File.Exists(Path.Combine(directory, ".project"));
        }

        private static async Task EnsurePlainWorkspaceAsync(string file, object connection)
        {
            // JPM-S129-JDT-IMPORT-VERIFY-20261124
            // Root bootstrap is independent from the old unmanaged-settings
            // guard CanManagePlain. That guard returns false for any .project,
            // including the VSIX's own Eclipse descriptor. Consequently the
            // previous code never told JDT LS about a root after JPM-S1.2.
            string initial;
            lock (Gate) initial = initializationRoot;
            string root = Path.GetDirectoryName(file);
            if (!string.IsNullOrWhiteSpace(initial) && IsInWorkspace(initial, root)) return;
            var loaded = JavaCpsWorkspaceContext.LoadedProjects();
            if (loaded.Length != 1 || !Equal(loaded[0], file) ||
                !Equal(JavaCpsWorkspaceContext.ProjectRoot, root) ||
                !JavaScopedClasspathBridge.CanBootstrapPlainWorkspace(file))
            {
                Log("Plain: bootstrap workspace Eclipse ignorato: root CPS o ownership non valida.");
                return;
            }
            // The LSP workspace is initially empty. Announce only the sole CPS
            // root; never remove existing folders or create Eclipse metadata.
            bool sent = await JavaLspRpc.NotifyForConnectionAsync(connection,
                "workspace/didChangeWorkspaceFolders", new
                {
                    @event = new
                    {
                        added = new[] { new {
                            uri = FileUri(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar),
                            name = Path.GetFileName(root)
                        } },
                        removed = new object[0]
                    }
                }).ConfigureAwait(false);
            if (sent && ReferenceEquals(connection, JavaLspRpc.ConnectionIdentity))
            {
                lock (Gate) if (initializationRoot == null) initializationRoot = root;
                Log("Plain: root CPS inviata a JDT LS (Eclipse VSIX); import asincrono. " + root);
            }
            else Log("Plain: registrazione root CPS non inviata.");
        }

        private static JArray PlainReferences(out string file)
        {
            file = null;
            var loaded = JavaCpsWorkspaceContext.LoadedProjects();
            if (loaded.Length != 1 || !CanManagePlain(loaded[0])) return null;
            lock (Gate)
            {
                var instance = Instances.FirstOrDefault(x => !x.disposed && Equal(x.project, loaded[0]) && Equal(x.backend, "Plain"));
                if (instance == null) return null;
                file = instance.project;
                // JDT LS uses glob patterns. Explicit full paths, forward slashes.
                return new JArray(instance.jars.Select(p => p.Replace('\\', '/')));
            }
        }

        private static JArray PlainSourceRoots(string projectFile)
        {
            lock (Gate)
            {
                var active = Instances.FirstOrDefault(x => !x.disposed &&
                    Equal(x.project, projectFile) && Equal(x.backend, "Plain"));
                if (active == null) return new JArray();
                string projectDirectory = Path.GetDirectoryName(projectFile);
                string prefix = "";
                if (IsInWorkspace(initializationRoot, projectDirectory))
                    prefix = projectDirectory.Substring(initializationRoot.TrimEnd('\\', '/').Length)
                        .TrimStart('\\', '/').Replace('\\', '/');
                return new JArray(active.sourceRoots.Select(path =>
                    prefix.Length == 0 ? path : prefix + "/" + path));
            }
        }

        private static void PutSourceRoots(JObject settings, JArray paths)
        {
            // JDT LS expects java.project.sourcePaths for an unmanaged folder.
            // Values must be project-relative, never absolute or nested in one another.
            settings.Remove("java.project.sourcePaths");
            var java = settings["java"] as JObject;
            if (java == null) settings["java"] = java = new JObject();
            var project = java["project"] as JObject;
            if (project == null) java["project"] = project = new JObject();
            java.Remove("project.sourcePaths");
            project["sourcePaths"] = paths.DeepClone();
        }

        private static void PutReferences(JObject settings, JToken references)
        {
            settings.Remove("java.project.referencedLibraries");
            var java = settings["java"] as JObject;
            if (java == null) settings["java"] = java = new JObject();
            var projectSettings = java["project"] as JObject;
            if (projectSettings == null) java["project"] = projectSettings = new JObject();
            java.Remove("project.referencedLibraries");
            projectSettings["referencedLibraries"] = references;
        }

        // Record the actual root sent with the LSP initialize request, not a
        // speculative CPS project snapshot that can arrive later.
        private static string RootFromInitialize(JToken request)
        {
            var value = request as JObject;
            if (value == null) return null;
            string uriText = (string)value["rootUri"];
            Uri uri;
            if (!string.IsNullOrWhiteSpace(uriText) &&
                Uri.TryCreate(uriText, UriKind.Absolute, out uri) && uri.IsFile)
            {
                try { return Path.GetFullPath(uri.LocalPath).TrimEnd('\\', '/'); }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException) { }
            }
            string path = (string)value["rootPath"];
            if (!string.IsNullOrWhiteSpace(path))
            {
                try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException) { }
            }
            return null;
        }

        internal static JToken PrepareInitialization(JToken original)
        {
            var initialSettings = (original as JObject)?.SelectToken("initializationOptions.settings") as JObject;
            string requestRoot = RootFromInitialize(original);
            lock (Gate)
            {
                initializationRoot = requestRoot ?? JavaCpsWorkspaceContext.ProjectRoot;
                appliedConnection = null;
                appliedPlainProject = null;
                nativeSettingsConnection = null;
                nativeSettings = initialSettings == null ? null : (JObject)initialSettings.DeepClone();
            }
            string file;
            var references = PlainReferences(out file);
            if (references == null || !(original is JObject)) return original;
            var request = (JObject)original.DeepClone();
            var options = request["initializationOptions"] as JObject;
            if (options == null) request["initializationOptions"] = options = new JObject();
            var settings = options["settings"] as JObject;
            if (settings == null) options["settings"] = settings = (JObject)JObject.FromObject(JavaLanguageClient.DefaultInitializationOptions)["settings"].DeepClone();
            PutReferences(settings, references);
            PutSourceRoots(settings, PlainSourceRoots(file));
            lock (Gate) appliedPlainProject = file;
            return request;
        }

        internal static JToken PrepareNativeConfiguration(JToken original)
        {
            var settings = (original as JObject)?["settings"] as JObject;
            if (settings == null || !HasLoadedProjects) return original;
            lock (Gate)
            {
                var connection = JavaLspRpc.ConnectionIdentity;
                if (nativeSettings == null || !ReferenceEquals(nativeSettingsConnection, connection))
                    nativeSettings = (JObject)settings.DeepClone();
                else nativeSettings.Merge(settings, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
                nativeSettingsConnection = connection;
            }
            string file;
            var references = PlainReferences(out file);
            if (references == null) return original;
            var clone = (JObject)original.DeepClone();
            PutReferences((JObject)clone["settings"], references);
            PutSourceRoots((JObject)clone["settings"], PlainSourceRoots(file));
            lock (Gate) { appliedPlainProject = file; appliedConnection = JavaLspRpc.ConnectionIdentity; }
            return clone;
        }

        private static async Task SendPlainSettingsAsync(object connection)
        {
            string file;
            var references = PlainReferences(out file);
            JObject settings = JObject.FromObject(JavaLanguageClient.DefaultInitializationOptions)["settings"] as JObject;
            lock (Gate)
            {
                if (references == null && (!ReferenceEquals(appliedConnection, connection) || appliedPlainProject == null)) return;
                if (ReferenceEquals(nativeSettingsConnection, connection) && nativeSettings != null)
                    settings.Merge(nativeSettings, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
            }
            // If the solution changes to multiple roots, remove our previous
            // absolute list. Restore the native preference when present.
            if (references != null)
            {
                PutReferences(settings, references);
                PutSourceRoots(settings, PlainSourceRoots(file));
            }
            else if (settings.SelectToken("java.project.referencedLibraries") == null && settings["java.project.referencedLibraries"] == null)
                PutReferences(settings, new JArray("lib/**/*.jar"));
            if (await JavaLspRpc.NotifyForConnectionAsync(connection, "workspace/didChangeConfiguration", new { settings }).ConfigureAwait(false))
            {
                lock (Gate) { appliedConnection = connection; appliedPlainProject = file; }
            }
        }

        public void Dispose()
        {
            FileSystemWatcher watcher;
            lock (Gate)
            {
                disposed = true;
                Instances.Remove(this);
                revision++;
                System.Threading.Interlocked.Increment(ref plainWatcherRevision);
                watcher = plainProjectWatcher;
                plainProjectWatcher = null;
                watchedProject = null;
            }
            watcher?.Dispose();
        }

        private static bool Equal(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        private static string FileUri(string value) => new Uri(Path.GetFullPath(value)).AbsoluteUri;
        private static string Stamp(string value)
        {
            var info = new FileInfo(value);
            return info.Exists ? info.LastWriteTimeUtc.Ticks + ":" + info.Length : null;
        }
        private static void Log(string value) => JavaLanguageStatus.LogLifecycle("[F4.E dependencies] " + value);
    }
}
