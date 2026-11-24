using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace JavaLanguageSupport.ProjectSystem.PackageManager
{
    // JPM-LOG-20261124: opt-in read-only tool output for Maven/Gradle projects.
    // Reuses the existing secure launcher; does not build or edit the project.
    internal static class JavaJpmToolDiagnostics
    {
        internal static async Task<int> RunAsync(string projectFile, string backend,
            CancellationToken token)
        {
            if (backend != "Maven" && backend != "Gradle")
                throw new NotSupportedException("Diagnostica esterna prevista per progetti Maven/Gradle. " +
                    "Per Plain i log Maven vengono acquisiti durante installazione e aggiornamento.");
            string root = Path.GetDirectoryName(Path.GetFullPath(projectFile));
            string helper = Path.Combine(Path.GetDirectoryName(typeof(JavaJpmToolDiagnostics)
                .Assembly.Location), "ProjectSystem", "BuildSystem", "JavaProject.ToolRunner.ps1");
            if (!File.Exists(helper))
                throw new FileNotFoundException("Launcher JavaProject.ToolRunner.ps1 non trovato", helper);
            string wrapper = Path.Combine(root, backend == "Maven" ? "mvnw.cmd" : "gradlew.bat");
            string command = File.Exists(wrapper) ? " -CommandPath \"" + wrapper + "\"" : "";
            var info = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + helper +
                "\" -Tool " + backend + " -Action Dependencies -ProjectDirectory \"" + root +
                "\"" + command)
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            JavaJpmProjectLogger.Info(projectFile, backend,
                "Avvio diagnostica dipendenze in sola lettura (nessuna modifica al build script).");
            using (var process = new Process { StartInfo = info, EnableRaisingEvents = true })
            {
                process.OutputDataReceived += (sender, args) =>
                {
                    if (args.Data != null) JavaJpmProjectLogger.Tool(projectFile, backend, args.Data);
                };
                process.ErrorDataReceived += (sender, args) =>
                {
                    if (args.Data != null) JavaJpmProjectLogger.Tool(projectFile, backend, args.Data, true);
                };
                if (!process.Start()) throw new IOException("Impossibile avviare PowerShell.");
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                using (token.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(); }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }))
                {
                    int code = await Task.Run(() =>
                    {
                        process.WaitForExit(); // also drains asynchronous stdout/stderr callbacks
                        return process.ExitCode;
                    }).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (code == 0) JavaJpmProjectLogger.Info(projectFile, backend,
                        "Diagnostica completata (exit code 0).");
                    else JavaJpmProjectLogger.Error(projectFile, backend,
                        "Diagnostica fallita: exit code " + code + ".");
                    return code;
                }
            }
        }
    }
}
