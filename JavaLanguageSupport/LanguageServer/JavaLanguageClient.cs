using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JavaLanguageSupport.ProjectSystem.Packages;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Workspace.VSIntegration.Contracts;
using Microsoft.VisualStudio.Utilities;

namespace JavaLanguageSupport.LanguageServer
{
    /// <summary>
    /// Integra Eclipse JDT LS nell'editor di Visual Studio (NON VS Code).
    /// Visual Studio gestisce initialize/didOpen/didChange/completion/hover/diagnostics.
    /// Il client fornisce lo stream JSON-RPC senza scriverci mai messaggi propri.
    /// </summary>
    [Export(typeof(ILanguageClient))]
    [ContentType("java")]
    public sealed class JavaLanguageClient : ILanguageClient, ILanguageClientCustomMessage2
    {
        // E3 Indexing Hotfix: root Open Folder (MEF import facoltativo).
        [Import(AllowDefault = true)]
        internal IVsFolderWorkspaceService FolderWorkspace = null;
        private Process serverProcess;
        // E3: keep one JDT LS data cache across VS restarts. The lease
        // stays open for this VS process, preventing concurrent use.
        private FileStream workspaceLease;
        private string reusableWorkspace;
        // Pack U2: un marker resta quando non abbiamo verificato un arresto pulito.
        // Non cancelliamo mai gli indici in automatico.
        private string workspacePendingMarker;
        private const string PendingMarkerName = ".java-vsix-session-pending";

        // U2.1: l'evento DTE rimane referenziato per tutta la vita dell'IDE.
        // StopServerAsync non e' un callback ILanguageClient automatico.
        private EnvDTE.DTEEvents visualStudioShutdownEvents;
        private int visualStudioShutdownHandled;

        public string Name => "Java Language Support (JDT LS)";
        public bool ShowNotificationOnInitializeFailed => true;

        // Con Visual Studio Open Folder, le opzioni di workspace si possono
        // personalizzare in .vs/VSWorkspaceSettings.json (vedi README).
        public IEnumerable<string> ConfigurationSections => new[] { "java" };
        public IEnumerable<string> FilesToWatch => new[]
        {
            "**/*.java", "**/pom.xml", "**/build.gradle", "**/build.gradle.kts",
            "**/settings.gradle", "**/settings.gradle.kts", "**/.classpath", "**/.project"
        };

        // JDT LS legge settings.java nell'initialize. Il formato e' annidato,
        // distinto dai nomi con punti usati in VSWorkspaceSettings.json.
        public object InitializationOptions => new
        {
            // JDT LS esclude le librerie JDK e i JAR da workspace/symbol
            // quando il client non annuncia classFileContentsSupport.
            // Il nostro selettore import usa tale ricerca per mostrare
            // *tutte* le classi omonime, incluse quelle della JRE.
            // L'apertura di URI jdt:// (navigazione ai .class) richiedera'
            // un ulteriore handler nell'editor: non e' implementata qui.
            extendedClientCapabilities = new
            {
                classFileContentsSupport = true
            },
            settings = new
            {
                java = new
                {
                    completion = new
                    {
                        enabled = true,
                        // JDT LS richiede un valore stringa: "auto", non il booleano true.
                        guessMethodArguments = "auto",
                        importOrder = new[] { "java", "javax", "org", "com" },
                        favoriteStaticMembers = new[] {
                            "org.junit.Assert.*", "org.junit.jupiter.api.Assertions.*"
                        }
                    },
                    // Maven/Gradle vengono importati per risolvere i simboli
                    // presenti nelle dipendenze del progetto.
                    import = new
                    {
                        maven = new { enabled = true },
                        gradle = new { enabled = true }
                    },
                    maven = new { downloadSources = true },
                    autobuild = new { enabled = true },
                    // Explicit JDT LS preferences: lenses and parameter hints.
                    referencesCodeLens = new { enabled = true },
                    implementationCodeLens = "all",
                    // JDT LS offers semantic inferred-type hints for Java 10+ var.
                    // Older versions may ignore variableTypes gracefully.
                    inlayHints = new
                    {
                        parameterNames = new { enabled = "all" },
                        variableTypes = new { enabled = true }
                    },
                    format = new { enabled = true },
                    configuration = new { updateBuildConfiguration = "automatic" }
                }
            }
        };

        // La middle layer e' solo osservativa: non modifica messaggi LSP.
        // I contatori nella voce Strumenti > Java: Stato IntelliSense
        // permettono di capire se hover/completamenti/codeAction arrivano a JDT LS.
        public object MiddleLayer => JavaLspDiagnosticsMiddleLayer.Instance;
        public object CustomMessageTarget => null;
        public Task AttachForCustomMessageAsync(StreamJsonRpc.JsonRpc rpc)
        {
            JavaLspRpc.Attach(rpc);
            return Task.CompletedTask;
        }

        public event AsyncEventHandler<EventArgs> StartAsync;
        public event AsyncEventHandler<EventArgs> StopAsync;

        public async Task OnLoadedAsync()
        {
            // Il server si attiva quando Visual Studio apre un file .java.
            var start = StartAsync;
            if (start != null)
                await start.InvokeAsync(this, EventArgs.Empty);

            // U2.1: registrazione effettiva al ciclo di vita dell'IDE. Non basta
            // definire StopServerAsync(): ILanguageClient non lo chiama da solo.
            await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                .SwitchToMainThreadAsync();
            HookVisualStudioShutdown();
        }

        // U2.1: evento VS reale, registrato sulla UI thread e mantenuto vivo.
        // Se la registrazione fallisce, il marker rimane conservativo: non
        // dichiarare mai "pulito" un workspace senza shutdown verificato.
        private void HookVisualStudioShutdown()
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
            if (visualStudioShutdownEvents != null) return;
            try
            {
                var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(
                    typeof(Microsoft.VisualStudio.Shell.Interop.SDTE)) as EnvDTE.DTE;
                if (dte == null || dte.Events == null || dte.Events.DTEEvents == null)
                {
                    JavaLanguageStatus.LogLifecycle(
                        "[U2.1 lifecycle] DTE shutdown non disponibile: marker conservato");
                    return;
                }
                visualStudioShutdownEvents = dte.Events.DTEEvents;
                visualStudioShutdownEvents.OnBeginShutdown += OnVisualStudioBeginShutdown;
                JavaLanguageStatus.LogLifecycle(
                    "[U2.1 lifecycle] DTE OnBeginShutdown registrato");
            }
            catch (Exception ex)
            {
                JavaLanguageStatus.LogLifecycle(
                    "[U2.1 lifecycle] registrazione shutdown fallita: " + ex);
            }
        }

        // DTE OnBeginShutdown e' sincrono. JTF.Run permette di attendere il
        // cleanup asincrono e di processare i passaggi richiesti dal thread UI
        // senza usare Task.Wait() (che puo' provocare un deadlock COM/LSP).
        private void OnVisualStudioBeginShutdown()
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
            if (Interlocked.Exchange(ref visualStudioShutdownHandled, 1) != 0)
                return;
            JavaLanguageStatus.LogLifecycle(
                "[U2.1 lifecycle] DTE OnBeginShutdown ricevuto");
            try
            {
                Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.Run(
                    async delegate { await StopServerAsync(); });
                JavaLanguageStatus.LogLifecycle(
                    "[U2.1 lifecycle] StopServerAsync completato");
            }
            catch (Exception ex)
            {
                // Il marker resta: non dichiarare pulito un workspace sul quale
                // il cleanup e' fallito. Mai eliminare indici o terminare Java.
                JavaLanguageStatus.LogLifecycle(
                    "[U2.1 lifecycle] StopServerAsync errore: " + ex);
            }
        }

        public async Task StopServerAsync()
        {
            // Pack U2: prima di invocare lo shutdown LSP drenare le notifiche
            // CPS; la semplice risposta a changeImportedProjects NON garantisce
            // che Eclipse abbia completato i job interni.
            JavaLanguageStatus.LogLifecycle("StopServerAsync: avvio; PID=" +
                (serverProcess == null ? "n/d" : serverProcess.Id.ToString()));
            bool drained = false;
            try
            {
                drained = await JavaWorkspaceRenameSync.BeginShutdownAndDrainAsync(12000)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                JavaLanguageStatus.LogLifecycle("drain CPS non riuscito: " + ex.Message);
            }

            bool lspShutdownCompleted = false;
            try
            {
                // Il client nativo VS invia la richiesta LSP 'shutdown'.
                var stop = StopAsync;
                if (stop != null)
                {
                    // U2.1: chiusura dell'IDE limitata nel tempo anche se
                    // Visual Studio non completa la richiesta LSP.
                    var request = stop.InvokeAsync(this, EventArgs.Empty);
                    var finished = await Task.WhenAny(request,
                        Task.Delay(12000)).ConfigureAwait(false);
                    if (ReferenceEquals(finished, request))
                    {
                        await request.ConfigureAwait(false);
                        lspShutdownCompleted = true;
                        JavaLanguageStatus.LogLifecycle("richiesta shutdown LSP completata");
                    }
                    else
                        JavaLanguageStatus.LogLifecycle(
                            "[U2.1 lifecycle] shutdown LSP timeout: marker conservato");
                }
                else
                {
                    // U2.3: StopAsync e' un evento che l'estensione invoca verso
                    // Visual Studio, NON una callback garantita. Durante
                    // OnBeginShutdown l'handler puo' essere gia' stato rimosso.
                    // In tal caso tentare il normale handshake LSP sul canale
                    // gia' esistente. Mai dichiarare pulita una cache se il
                    // canale e' assente/disposto o se shutdown non risponde.
                    JavaLanguageStatus.LogLifecycle(
                        "[U2.3 lifecycle] StopAsync senza handler; RPC connessa=" +
                        JavaLspRpc.Connected);
                    using (var timeout = new CancellationTokenSource(
                        TimeSpan.FromSeconds(12)))
                    {
                        lspShutdownCompleted = await JavaLspRpc.ShutdownStrictAsync(
                            timeout.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                JavaLanguageStatus.LogLifecycle("shutdown LSP non riuscito: " + ex);
            }

            // Eclipse JDT LS in 'exit' attende il suo shutdownJob (incluso
            // il salvataggio del modello); senza exit la sola 'shutdown'
            // puo' lasciare il processo vivo durante la chiusura di VS.
            if (lspShutdownCompleted && serverProcess != null && !serverProcess.HasExited)
            {
                try
                {
                    bool sent = await JavaLspRpc.NotifyAsync("exit", new { })
                        .ConfigureAwait(false);
                    JavaLanguageStatus.LogLifecycle("exit LSP inviato=" + sent);
                }
                catch (Exception ex)
                {
                    JavaLanguageStatus.LogLifecycle("exit LSP non inviato: " + ex.Message);
                }
            }

            Process process = serverProcess;
            bool exited = process == null;
            int exitCode = -1;
            if (process != null)
            {
                try
                {
                    // Non bloccare il thread UI mentre Eclipse salva .metadata.
                    // Non usare Kill(): interrompere il salvataggio puo' danneggiare la cache.
                    exited = await Task.Run(() => process.WaitForExit(20000))
                        .ConfigureAwait(false);
                    if (exited) exitCode = process.ExitCode;
                    JavaLanguageStatus.LogLifecycle("processo java uscito=" + exited +
                        "; exitCode=" + (exited ? exitCode.ToString() : "in esecuzione"));
                }
                catch (Exception ex)
                {
                    JavaLanguageStatus.LogLifecycle("attesa processo non riuscita: " + ex);
                    exited = false;
                }
            }
            // U2.2: la prima java.exe puo' essere un launcher che avvia
            // una seconda JVM. Prima di dichiarare pulito il workspace, aspetta
            // che NESSUN JDT LS usi ancora il medesimo percorso -data.
            bool javaTreeExited = exited &&
                await WaitForWorkspaceJdtProcessesExitAsync(reusableWorkspace, 12000)
                    .ConfigureAwait(false);
            JavaLanguageStatus.LogLifecycle(
                "[U2.2 lifecycle] processi JDT LS sul workspace terminati=" + javaTreeExited);
            bool eclipseReleased = javaTreeExited &&
                await WaitForEclipseLockReleaseAsync(reusableWorkspace, 8000)
                    .ConfigureAwait(false);
            JavaLanguageStatus.LogLifecycle("[U2.1 lifecycle] lock Eclipse rilasciato=" +
                eclipseReleased);
            bool clean = drained && lspShutdownCompleted && exited &&
                exitCode == 0 && javaTreeExited && eclipseReleased;
            if (clean && !string.IsNullOrEmpty(workspacePendingMarker))
            {
                try
                {
                    File.Delete(workspacePendingMarker);
                    JavaLanguageStatus.LogLifecycle("workspace persistente marcato pulito");
                    workspacePendingMarker = null;
                }
                catch (Exception ex)
                {
                    JavaLanguageStatus.LogLifecycle("marker mantenuto: " + ex.Message);
                }
            }
            else if (!clean)
            {
                JavaLanguageStatus.LogLifecycle(
                    "arresto non verificato: marker mantenuto; la cache non sara' riutilizzata");
            }

            JavaLspRpc.Detach();
            JavaDiagnosticsMonitor.Reset();
            JavaSemanticLegend.Reset();
            JavaWorkspaceActivity.Stopped();
            JavaLanguageStatus.Stopped();
            // Se Java e' ancora vivo, manteniamo il lease fino alla fine del
            // processo VS. Il marker impedisce il riuso dopo un exit incompleto.
            if (exited && workspaceLease != null)
            {
                workspaceLease.Dispose();
                workspaceLease = null;
                reusableWorkspace = null;
            }
            if (exited) serverProcess = null;
        }

        // U2.1: non modificare/eliminare mai il file .metadata/.lock.
        // Sul workspace Windows il lock e' detenuto dal processo Eclipse vivo;
        // attenderne la liberazione aiuta a riconoscere launcher java padre/figlio.
        // In assenza del lock non dichiarare la sessione verificata.
        private static async Task<bool> WaitForEclipseLockReleaseAsync(
            string workspace, int milliseconds)
        {
            if (string.IsNullOrWhiteSpace(workspace)) return false;
            string file = Path.Combine(workspace, ".metadata", ".lock");
            return await Task.Run(() =>
            {
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
                do
                {
                    // U2.2: un lock inesistente NON equivale a un lock
                    // ancora detenuto; prima e' gia' stata verificata via WMI
                    // l'assenza di ogni JVM JDT LS sullo stesso -data.
                    if (!File.Exists(file)) return true;
                    try
                    {
                        using (var probe = new FileStream(file, FileMode.Open,
                            FileAccess.ReadWrite, FileShare.None)) { }
                        return true;
                    }
                    catch (IOException) { /* Il server potrebbe essere ancora attivo. */ }
                    catch (UnauthorizedAccessException) { return false; }
                    System.Threading.Thread.Sleep(200);
                } while (DateTime.UtcNow < deadline);
                return false;
            }).ConfigureAwait(false);
        }

        // U2.2: la Process.WaitForExit del launcher non prova che la
        // JVM figlia sia uscita. Windows WMI permette di identificare ogni
        // java.exe/javaw.exe con lo stesso argomento -data. Se la query fallisce,
        // il marker pending RESTA: non si certifica uno shutdown ignoto.
        private static async Task<bool> WaitForWorkspaceJdtProcessesExitAsync(
            string workspace, int milliseconds)
        {
            if (string.IsNullOrWhiteSpace(workspace)) return false;
            return await Task.Run(() =>
            {
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
                do
                {
                    try
                    {
                        bool running = false;
                        using (var searcher = new System.Management.ManagementObjectSearcher(
                            "SELECT CommandLine FROM Win32_Process WHERE Name='java.exe' OR Name='javaw.exe'"))
                        using (var rows = searcher.Get())
                        {
                            foreach (System.Management.ManagementObject row in rows)
                            {
                                try
                                {
                                    string command = row["CommandLine"] as string;
                                    if (string.IsNullOrWhiteSpace(command)) continue;
                                    if (command.IndexOf(workspace, StringComparison.OrdinalIgnoreCase) >= 0 &&
                                        command.IndexOf("org.eclipse.jdt.ls.core.id1",
                                            StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        running = true;
                                        break;
                                    }
                                }
                                finally { row.Dispose(); }
                            }
                        }
                        if (!running) return true;
                    }
                    catch (Exception ex)
                    {
                        JavaLanguageStatus.LogLifecycle(
                            "[U2.2 lifecycle] impossibile verificare processi JDT LS: " + ex.Message);
                        return false;
                    }
                    System.Threading.Thread.Sleep(250);
                } while (DateTime.UtcNow < deadline);
                return false;
            }).ConfigureAwait(false);
        }

        public Task OnServerInitializedAsync()
        {
            JavaLanguageStatus.Initialized();
            JavaWorkspaceActivity.Initialized();
            return Task.CompletedTask;
        }

        public Task<InitializationFailureContext> OnServerInitializeFailedAsync(
            ILanguageClientInitializationInfo initializationState)
        {
            var exception = initializationState == null ? null : initializationState.InitializationException;
            var error = exception == null ? "Verificare il log del server." : exception.ToString();
            JavaLanguageStatus.Failed(error);
            return Task.FromResult(new InitializationFailureContext
            {
                FailureMessage = "Impossibile inizializzare Java Language Server. " +
                    "Verifica JAVA_HOME (JDK 21+), JDTLS_HOME e Strumenti > Java: Stato IntelliSense. " +
                    (exception == null ? "" : exception.Message)
            });
        }

        public async Task<Connection> ActivateAsync(CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return StartJdtLs(cancellationToken);
            }
            catch (Exception ex)
            {
                JavaLanguageStatus.Failed(ex.ToString());
                throw;
            }
        }

        // E3 Indexing Hotfix: per-Open-Folder cache. Never reuse an Eclipse
        // metadata directory belonging to an unrelated Java workspace.
        private string WorkspaceCacheScope()
        {
            string folder = null;
            try
            {
                var workspace = FolderWorkspace == null ? null : FolderWorkspace.CurrentWorkspace;
                folder = workspace == null ? null : workspace.Location;
            }
            catch (Exception) { /* Open Folder service may be unavailable. */ }
            // CPS .javaproj is an actual Java workspace even if VS did not
            // enter Open Folder mode. Isolate its JDT LS cache accordingly.
            if (string.IsNullOrWhiteSpace(folder))
                folder = JavaCpsWorkspaceContext.ProjectRoot;
            if (string.IsNullOrWhiteSpace(folder)) return "unscoped";
            try
            {
                string canonical = Path.GetFullPath(folder).TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .ToUpperInvariant();
                using (var sha = SHA256.Create())
                {
                    byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                    // Compact stable ID, no user path embedded in folder name.
                    return "ws-" + BitConverter.ToString(bytes, 0, 16)
                        .Replace("-", "").ToLowerInvariant();
                }
            }
            catch (Exception) { return "unscoped"; }
        }

        // E3 Indexing Hotfix: lease remains open until StopServerAsync;
        // concurrent VS instances NEVER use the same JDT LS -data path.
        // La cache che ha un marker di sessione non terminata NON viene
        // riutilizzata: potrebbe contenere .index/.safetable incoerenti.
        // Manteniamo il folder vecchio per diagnosi, senza eliminarlo.
        private string AcquireReusableWorkspace()
        {
            if (workspaceLease != null && !string.IsNullOrEmpty(reusableWorkspace))
            {
                if (serverProcess != null && !serverProcess.HasExited)
                    throw new InvalidOperationException(
                        "JDT LS precedente ancora in esecuzione: impossibile riusare -data.");
                if (File.Exists(Path.Combine(reusableWorkspace, PendingMarkerName)))
                {
                    workspaceLease.Dispose();
                    workspaceLease = null;
                    reusableWorkspace = null;
                }
                else return reusableWorkspace;
            }
            string root = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
                "JavaLanguageSupportVS", "jdtls", "persistent", WorkspaceCacheScope());
            Directory.CreateDirectory(root);
            // U2.1: gli slot U2 precedenti possono contenere marker
            // diagnostici; non riusarli forzatamente e non ripiegare subito su
            // directory process-PID-GUID che non vengono riutilizzate.
            for (int slot = 0; slot < 32; slot++)
            {
                string folder = Path.Combine(root, "slot-" + slot);
                Directory.CreateDirectory(folder);
                try
                {
                    FileStream lease = new FileStream(Path.Combine(folder,
                        ".java-vsix-lease"), FileMode.OpenOrCreate,
                        FileAccess.ReadWrite, FileShare.None);
                    if (File.Exists(Path.Combine(folder, PendingMarkerName)))
                    {
                        lease.Dispose();
                        JavaLanguageStatus.LogLifecycle(
                            "cache con arresto non verificato: " + folder + " (ignorata)");
                        continue;
                    }
                    workspaceLease = lease;
                    reusableWorkspace = folder;
                    return folder;
                }
                catch (IOException) { /* Un'altra istanza VS possiede la cache. */ }
            }
            // Ultima risorsa: workspace isolato e univoco. Mai riusare un
            // percorso process-PID se il PID e' stato riciclato da Windows.
            string isolated = Path.Combine(root, "process-" +
                Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(isolated);
            workspaceLease = new FileStream(Path.Combine(isolated,
                ".java-vsix-lease"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            reusableWorkspace = isolated;
            return isolated;
        }

        private Connection StartJdtLs(CancellationToken cancellationToken)
        {
            string jdtlsHome = Environment.GetEnvironmentVariable("JDTLS_HOME");
            if (string.IsNullOrWhiteSpace(jdtlsHome))
                throw new InvalidOperationException(
                    "JDTLS_HOME non impostata. Deve indicare la cartella estratta di eclipse.jdt.ls.");

            jdtlsHome = Path.GetFullPath(jdtlsHome.Trim().Trim('"'));
            string pluginsFolder = Path.Combine(jdtlsHome, "plugins");
            string configFolder = Path.Combine(jdtlsHome, "config_win");
            if (!Directory.Exists(pluginsFolder) || !Directory.Exists(configFolder))
                throw new DirectoryNotFoundException(
                    "JDTLS_HOME deve contenere plugins e config_win: " + jdtlsHome);

            string launcher = Directory.GetFiles(pluginsFolder, "org.eclipse.equinox.launcher_*.jar")
                .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (launcher == null)
                throw new FileNotFoundException("Launcher Equinox non trovato in " + pluginsFolder);

            string java = "java.exe";
            string javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrWhiteSpace(javaHome))
            {
                java = Path.Combine(javaHome.Trim().Trim('"'), "bin", "java.exe");
                if (!File.Exists(java))
                    throw new FileNotFoundException("java.exe non trovato in JAVA_HOME: " + java);
            }

            // Reuse on-disk JDT LS metadata between IDE launches without
            // sharing a live workspace between simultaneous VS processes.
            string dataFolder = AcquireReusableWorkspace();
            JavaLanguageStatus.Starting(java, launcher, dataFolder);
            JavaWorkspaceRenameSync.BeginSession();
            workspacePendingMarker = Path.Combine(dataFolder, PendingMarkerName);
            // Marker PRIMA di avviare Java: crash/chiusura forzata lasciano la cache
            // intatta ma impediscono di riutilizzarla al prossimo avvio.
            File.WriteAllText(workspacePendingMarker,
                "VS PID=" + Process.GetCurrentProcess().Id +
                "; avvio=" + DateTime.UtcNow.ToString("O") + Environment.NewLine);
            JavaLanguageStatus.LogLifecycle("workspace pending=" + workspacePendingMarker);
            JavaDiagnosticsMonitor.Reset(); // Pack A3: clear stale diagnostics on restart.
            JavaSemanticLegend.Reset();
            JavaWorkspaceActivity.Starting();

            string args =
                "-Declipse.application=org.eclipse.jdt.ls.core.id1 " +
                "-Dosgi.bundles.defaultStartLevel=4 " +
                "-Declipse.product=org.eclipse.jdt.ls.core.product " +
                "-Dlog.level=INFO " +
                "-Xmx1G " +
                "--add-modules=ALL-SYSTEM " +
                "--add-opens java.base/java.util=ALL-UNNAMED " +
                "--add-opens java.base/java.lang=ALL-UNNAMED " +
                "-jar " + Quote(launcher) + " " +
                "-configuration " + Quote(configFolder) + " " +
                "-data " + Quote(dataFolder);

            var info = new ProcessStartInfo
            {
                FileName = java,
                Arguments = args,
                WorkingDirectory = jdtlsHome,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            // Se le variabili sono definite da un'altra applicazione,
            // JDT LS passerebbe a TCP e ignorerebbe stdin/stdout.
            info.EnvironmentVariables.Remove("CLIENT_PORT");
            info.EnvironmentVariables.Remove("CLIENT_HOST");

            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.ErrorDataReceived += (sender, e) => JavaLanguageStatus.LogStderr(e.Data);
            process.Exited += (sender, e) =>
            {
                try { JavaLanguageStatus.Exited(process.ExitCode); }
                catch (InvalidOperationException) { }
            };

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("Avvio di JDT LS non riuscito.");
            }
            catch
            {
                process.Dispose();
                // Non cancellare il marker: la sessione non e' stata completata.
                // Rilascia il lease per una futura attivazione su altro slot.
                workspaceLease?.Dispose();
                workspaceLease = null;
                reusableWorkspace = null;
                throw;
            }
            serverProcess = process;
            JavaLanguageStatus.Started(process.Id);
            process.BeginErrorReadLine();
            return new Connection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        }

        private static string Quote(string argument) => "\"" + argument.Replace("\"", "\\\"") + "\"";
    }
}
