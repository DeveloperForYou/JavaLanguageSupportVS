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

namespace JavaLanguageSupport
{
    /// <summary>
    /// Client LSP per Eclipse JDT LS. Java e il language server sono installazioni
    /// ESTERNE al VSIX (JAVA_HOME e JDTLS_HOME).
    /// </summary>
    [Export(typeof(ILanguageClient))]
    [ContentType("java")]
    public sealed class JavaLanguageClient : ILanguageClient
    {
        private Process serverProcess;

        public string Name => "Java Language Support (JDT LS)";
        public IEnumerable<string> ConfigurationSections => null;
        public object InitializationOptions => null;
        public IEnumerable<string> FilesToWatch => null;
        public bool ShowNotificationOnInitializeFailed => true;

        public event AsyncEventHandler<EventArgs> StartAsync;
        public event AsyncEventHandler<EventArgs> StopAsync;

        public async Task OnLoadedAsync()
        {
            var start = StartAsync;
            if (start != null)
                await start.InvokeAsync(this, EventArgs.Empty);
        }

        public async Task StopServerAsync()
        {
            var stop = StopAsync;
            if (stop != null)
                await stop.InvokeAsync(this, EventArgs.Empty);
        }

        public Task OnServerInitializedAsync() => Task.CompletedTask;

        public Task<InitializationFailureContext> OnServerInitializeFailedAsync(
            ILanguageClientInitializationInfo initializationState)
        {
            var exception = initializationState.InitializationException;
            return Task.FromResult(new InitializationFailureContext
            {
                FailureMessage = "Impossibile avviare Java Language Server. " +
                    "Verifica JAVA_HOME (JDK 21+), JDTLS_HOME e i file in JDTLS_HOME. " +
                    (exception == null ? string.Empty : exception.Message)
            });
        }

        public async Task<Connection> ActivateAsync(CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();

            string jdtlsHome = Environment.GetEnvironmentVariable("JDTLS_HOME");
            if (string.IsNullOrWhiteSpace(jdtlsHome))
                throw new InvalidOperationException(
                    "Variabile JDTLS_HOME non impostata. Deve indicare la cartella estratta di eclipse.jdt.ls.");

            jdtlsHome = Path.GetFullPath(jdtlsHome.Trim().Trim('"'));
            string pluginsFolder = Path.Combine(jdtlsHome, "plugins");
            string configFolder = Path.Combine(jdtlsHome, "config_win");
            if (!Directory.Exists(pluginsFolder) || !Directory.Exists(configFolder))
                throw new DirectoryNotFoundException(
                    "JDTLS_HOME deve contenere le cartelle plugins e config_win: " + jdtlsHome);

            string launcher = Directory.GetFiles(
                pluginsFolder, "org.eclipse.equinox.launcher_*.jar")
                .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (launcher == null)
                throw new FileNotFoundException(
                    "Launcher Equinox non trovato in " + pluginsFolder);

            string java = "java.exe";
            string javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrWhiteSpace(javaHome))
            {
                java = Path.Combine(javaHome.Trim().Trim('"'), "bin", "java.exe");
                if (!File.Exists(java))
                    throw new FileNotFoundException("java.exe non trovato in JAVA_HOME: " + java);
            }

            // Impedisce che due processi Visual Studio usino contemporaneamente
            // lo stesso workspace interno di JDT LS.
            string dataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JavaLanguageSupportVS", "jdtls", Process.GetCurrentProcess().Id.ToString());
            Directory.CreateDirectory(dataFolder);

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

            // Nessuna variabile CLIENT_PORT: JDT LS comunica su stdin/stdout.
            info.EnvironmentVariables.Remove("CLIENT_PORT");
            info.EnvironmentVariables.Remove("CLIENT_HOST");

            var process = new Process { StartInfo = info };
            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    Trace.WriteLine("[JDT LS] " + e.Data);
            };

            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start())
                throw new InvalidOperationException("Avvio del processo JDT LS non riuscito.");

            serverProcess = process;
            process.BeginErrorReadLine();
            return new Connection(
                process.StandardOutput.BaseStream,
                process.StandardInput.BaseStream);
        }

        private static string Quote(string argument)
        {
            // I percorsi generati qui non terminano in backslash.
            return "\"" + argument.Replace("\"", "\\\"") + "\"";
        }
    }
}
