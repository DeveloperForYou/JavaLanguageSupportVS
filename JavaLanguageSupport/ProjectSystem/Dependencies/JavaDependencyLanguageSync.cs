using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            lock (Gate)
            {
                if (disposed) return;
                Instances.Add(this);
                if (Equal(project, file) && Equal(backend, kind) && Equal(requestedJdk, jdk) &&
                    jars.SequenceEqual(references, StringComparer.OrdinalIgnoreCase) &&
                    sourceRoots.SequenceEqual(roots, StringComparer.OrdinalIgnoreCase)) return;
                if (!Equal(project, file) || !Equal(requestedJdk, jdk)) resolvedJdk = null;
                project = file;
                backend = kind;
                requestedJdk = jdk;
                jars = references;
                sourceRoots = roots;
                QueueLocked();
            }
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
                instance.Signal(file);
                _ = Task.Run(() =>
                {
                    try { instance.refreshTree(); }
                    catch (Exception ex) { Log("Ricarica albero: " + ex.GetType().Name); }
                });
            }
            Log("Ricarica dipendenze richiesta.");
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
                    string[] updated = jars.Where(p => !p.StartsWith(managedRoot,
                            StringComparison.OrdinalIgnoreCase))
                        .Concat(declared).Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
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
            // JPM-S1.2-JDTLS-SCOPED-ECLIPSE: imported Eclipse projects use the
            // source/test library flags in .classpath, not a shared unmanaged
            // referencedLibraries list. Keep the existing Maven/Gradle flows.
            if (scoped != null && scoped.Active)
            {
                bool notify = scoped.Changed || !ReferenceEquals(previousConnection, connection);
                if (notify)
                {
                    var scopedChanges = new[]
                    {
                        new { uri = FileUri(Path.Combine(root, ".classpath")), type = scoped.Created ? 1 : 2 },
                        new { uri = FileUri(Path.Combine(root, ".project")), type = scoped.Created ? 1 : 2 }
                    };
                    await JavaLspRpc.NotifyForConnectionAsync(connection,
                        "workspace/didChangeWatchedFiles", new { changes = scopedChanges }).ConfigureAwait(false);
                    if (!IsCurrent(file, version, connection)) return;
                    string scopedRootUri = FileUri(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar);
                    using (var scopedTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
                        await JavaLspRpc.CommandForConnectionAsync(connection,
                            "java.project.changeImportedProjects",
                            new object[] { new string[0], new[] { scopedRootUri }, new string[0] },
                            scopedTimeout.Token).ConfigureAwait(false);
                    if (!IsCurrent(file, version, connection)) return;
                    await JavaLspRpc.NotifyForConnectionAsync(connection,
                        "java/projectConfigurationUpdate",
                        new { uri = FileUri(Path.Combine(root, ".classpath")) }).ConfigureAwait(false);
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
                }
                Log("Plain: JDT LS scoped Eclipse classpath richiesto; JAR=" + references.Length + ".");
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
            string initial;
            lock (Gate) initial = initializationRoot;
            if (!string.IsNullOrWhiteSpace(initial) || !CanManagePlain(file)) return;
            string root = Path.GetDirectoryName(file);
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
                Log("Plain: root CPS registrata dopo initialize; import JDT LS asincrono.");
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
            lock (Gate) { disposed = true; Instances.Remove(this); revision++; }
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
