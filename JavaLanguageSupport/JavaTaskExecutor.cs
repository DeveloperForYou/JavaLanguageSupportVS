using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace JavaLanguageSupport
{
    /// <summary>
    /// Compila/esegue Java, Maven e Gradle senza avviare una shell arbitraria.
    /// Solo wrapper locali dei progetti e tool standard presenti nel PATH.
    /// </summary>
    internal sealed class JavaTaskExecutor
    {
        private readonly Action<string> log;
        public JavaTaskExecutor(Action<string> logger) { log = logger ?? throw new ArgumentNullException(nameof(logger)); }

        public async Task<int> ExecuteAsync(JavaWorkspace workspace, JavaAction action, CancellationToken token)
        {
            switch (workspace.Kind)
            {
                case JavaProjectKind.Maven:
                    return await MavenAsync(workspace, action, token);
                case JavaProjectKind.Gradle:
                    return await GradleAsync(workspace, action, token);
                default:
                    return await SingleFileAsync(workspace, action, token);
            }
        }

        private async Task<int> MavenAsync(JavaWorkspace ws, JavaAction action, CancellationToken token)
        {
            bool wrapper = File.Exists(Path.Combine(ws.RootFolder, "mvnw.cmd"));
            string command = wrapper ? Path.Combine(ws.RootFolder, "mvnw.cmd") : "mvn.cmd";
            string args;
            switch (action)
            {
                case JavaAction.Build: args = "-B -DskipTests package"; break;
                case JavaAction.Test: args = "-B test"; break;
                default:
                    if (string.IsNullOrWhiteSpace(ws.MainClass))
                        throw new InvalidOperationException("Per eseguire Maven apri la classe Java che contiene main().");
                    // Richiede accesso al plugin org.codehaus.mojo:exec-maven-plugin.
                    args = "-B compile exec:java -Dexec.mainClass=" + ws.MainClass;
                    break;
            }
            return await RunBatchAsync(command, args, ws.RootFolder, token);
        }

        private async Task<int> GradleAsync(JavaWorkspace ws, JavaAction action, CancellationToken token)
        {
            bool wrapper = File.Exists(Path.Combine(ws.RootFolder, "gradlew.bat"));
            string command = wrapper ? Path.Combine(ws.RootFolder, "gradlew.bat") : "gradle.bat";
            // 'run' richiede Gradle application plugin e mainClass configurata in build.gradle.
            string args = action == JavaAction.Build ? "build" :
                          action == JavaAction.Test ? "test" : "run";
            return await RunBatchAsync(command, args, ws.RootFolder, token);
        }

        private async Task<int> SingleFileAsync(JavaWorkspace ws, JavaAction action, CancellationToken token)
        {
            if (action == JavaAction.Test)
                throw new InvalidOperationException("I test richiedono un progetto Maven/Gradle (JUnit o TestNG).");

            string javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
            string javac = string.IsNullOrWhiteSpace(javaHome) ? "javac.exe" :
                Path.Combine(javaHome.Trim().Trim('"'), "bin", "javac.exe");
            string java = string.IsNullOrWhiteSpace(javaHome) ? "java.exe" :
                Path.Combine(javaHome.Trim().Trim('"'), "bin", "java.exe");
            string output = Path.Combine(ws.RootFolder, ".java-vs", "classes");
            Directory.CreateDirectory(output);
            int compile = await RunProcessAsync(javac,
                "-encoding UTF-8 -d " + Quote(output) + " " + Quote(ws.SourceFile),
                ws.RootFolder, token);
            if (compile != 0 || action == JavaAction.Build)
                return compile;
            log("-- Esecuzione: " + ws.MainClass);
            return await RunProcessAsync(java, "-cp " + Quote(output) + " " + ws.MainClass,
                ws.RootFolder, token);
        }

        private Task<int> RunBatchAsync(string filename, string arguments, string folder, CancellationToken token)
        {
            // .bat/.cmd richiedono cmd.exe. Wrapper relativo al workspace affidato esplicitamente all'utente.
            // Gli argomenti provengono solo da comandi fissi o da nomi classe validati.
            string cmdArgs = "/d /s /c \"\"" + filename + "\" " + arguments + "\"";
            return RunProcessAsync("cmd.exe", cmdArgs, folder, token);
        }

        private async Task<int> RunProcessAsync(string executable, string arguments,
            string workingDirectory, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            log("> " + executable + " " + arguments);
            var info = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using (var process = new Process { StartInfo = info })
            {
                if (!process.Start())
                    throw new InvalidOperationException("Impossibile avviare " + executable);

                // Drenaggio simultaneo stdout/stderr: Maven può generare moltissimo output.
                Task stdout = CopyLinesAsync(process.StandardOutput, token);
                Task stderr = CopyLinesAsync(process.StandardError, token);
                Task completion = Task.Run(() => process.WaitForExit(), token);
                await Task.WhenAll(stdout, stderr, completion);
                return process.ExitCode;
            }
        }

        private async Task CopyLinesAsync(StreamReader stream, CancellationToken token)
        {
            string line;
            while ((line = await stream.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                token.ThrowIfCancellationRequested();
                log(line);
            }
        }

        private static string Quote(string value)
        {
            // Restringe i caratteri critici per cmd al nome file percorso fornito da Windows.
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }
}
