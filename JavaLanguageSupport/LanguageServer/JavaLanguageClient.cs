using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Threading;
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
        private Process serverProcess;

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
        }

        public async Task StopServerAsync()
        {
            // Visual Studio esegue prima shutdown/exit via LSP.
            var stop = StopAsync;
            if (stop != null)
                await stop.InvokeAsync(this, EventArgs.Empty);
            JavaLspRpc.Detach();
            JavaLanguageStatus.Stopped();
        }

        public Task OnServerInitializedAsync()
        {
            JavaLanguageStatus.Initialized();
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

            // Workspace interno isolato: piu' istanze di Visual Studio non
            // devono condividere contemporaneamente la stessa cartella -data.
            string dataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JavaLanguageSupportVS", "jdtls", Process.GetCurrentProcess().Id.ToString());
            Directory.CreateDirectory(dataFolder);
            JavaLanguageStatus.Starting(java, launcher, dataFolder);

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
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("Avvio di JDT LS non riuscito.");
            }
            serverProcess = process;
            JavaLanguageStatus.Started(process.Id);
            process.BeginErrorReadLine();
            return new Connection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        }

        private static string Quote(string argument) => "\"" + argument.Replace("\"", "\\\"") + "\"";
    }
}
