using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace JavaLanguageSupport.ProjectSystem.PackageManager
{
    // JPM-LOG-20261124: one VS Output pane and one on-disk log PER .javaproj.
    // Nothing is printed into the Package Manager tabs. File failures cannot break package changes.
    internal static class JavaJpmProjectLogger
    {
        private sealed class OutputLine
        {
            internal string Project, Message;
            internal bool Activate;
        }
        private static readonly object FileGate = new object();
        private static readonly ConcurrentQueue<OutputLine> Pending = new ConcurrentQueue<OutputLine>();
        private static int draining;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private const long MaxLogBytes = 8L * 1024 * 1024;
        // Basic secret scrubbing for tool output; avoid logging POM/settings.xml contents.
        private static readonly Regex Secrets = new Regex(
            @"(?i)\b(password|passwd|token|secret|authorization|api[_-]?key)\b(\s*[:=]\s*)([^\s]+)",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex UrlCredentials = new Regex(
            @"(?i)(https?://)[^\s/@]+:[^\s/@]+@",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        internal static void Info(string project, string component, string message)
        { Write(project, "INFO", component, message, false); }
        internal static void Warning(string project, string component, string message)
        { Write(project, "WARN", component, message, true); }
        internal static void Error(string project, string component, Exception error)
        { Write(project, "ERROR", component, error == null ? "Errore sconosciuto." : error.ToString(), true); }
        internal static void Error(string project, string component, string message)
        { Write(project, "ERROR", component, message, true); }
        internal static void Tool(string project, string tool, string line, bool standardError = false)
        { Write(project, standardError ? "STDERR" : "STDOUT", tool, line, false); }

        internal static void Transcript(string project, string tool, string stdout, string stderr)
        {
            using (var reader = new StringReader(stdout ?? ""))
            {
                string line;
                while ((line = reader.ReadLine()) != null) Tool(project, tool, line);
            }
            using (var reader = new StringReader(stderr ?? ""))
            {
                string line;
                while ((line = reader.ReadLine()) != null) Tool(project, tool, line, true);
            }
        }

        internal static void Open(string project)
        {
            string full;
            if (!TryProject(project, out full)) return;
            Pending.Enqueue(new OutputLine { Project = full, Activate = true });
            QueueDrain();
        }

        private static bool TryProject(string project, out string full)
        {
            full = null;
            if (string.IsNullOrWhiteSpace(project)) return false;
            try
            {
                string candidate = Path.GetFullPath(project);
                if (!string.Equals(Path.GetExtension(candidate), ".javaproj",
                    StringComparison.OrdinalIgnoreCase)) return false;
                full = candidate;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException ||
                ex is PathTooLongException) { return false; }
        }

        private static string HashId(string full)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Utf8.GetBytes(full.ToUpperInvariant())))
                    .Replace("-", "").ToLowerInvariant();
        }

        private static string LogPath(string full)
        {
            return Path.Combine(Path.GetDirectoryName(full), ".java-vs", "logs",
                "jpm-" + Path.GetFileNameWithoutExtension(full) + ".log");
        }

        private static void AppendFile(string full, string record)
        {
            lock (FileGate)
            {
                string path = LogPath(full);
                try { AppendRotated(path, record); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                    ex is ArgumentException || ex is NotSupportedException)
                {
                    // Read-only checkout: fallback remains isolated per exact project path.
                    string fallback = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "JavaLanguageSupportVS", "logs", "jpm", HashId(full), "jpm.log");
                    try { AppendRotated(fallback, record); }
                    catch (Exception fallbackError)
                    { Debug.WriteLine("JPM: log file non scrivibile: " + fallbackError.Message); }
                }
            }
        }

        private static void AppendRotated(string path, string record)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (File.Exists(path) && new FileInfo(path).Length > MaxLogBytes)
            {
                for (int n = 3; n >= 1; n--)
                {
                    string from = path + "." + n;
                    string to = path + "." + (n + 1);
                    if (File.Exists(from))
                    {
                        if (n == 3) File.Delete(from);
                        else { if (File.Exists(to)) File.Delete(to); File.Move(from, to); }
                    }
                }
                File.Move(path, path + ".1");
            }
            File.AppendAllText(path, record, Utf8);
        }

        private static void Write(string project, string level, string component,
            string message, bool activate)
        {
            string full;
            if (!TryProject(project, out full)) return;
            string sanitized = UrlCredentials.Replace(message ?? "", "$1***:***@");
            sanitized = Secrets.Replace(sanitized, "$1$2***");
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
            string prefix = stamp + " [" + level + "] [" + component + "] ";
            var sb = new StringBuilder();
            using (var reader = new StringReader(sanitized))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                    sb.Append(prefix).AppendLine(line);
            }
            if (sb.Length == 0) sb.Append(prefix).AppendLine();
            string record = sb.ToString();
            AppendFile(full, record);
            Pending.Enqueue(new OutputLine { Project = full, Message = record, Activate = activate });
            QueueDrain();
        }

        private static void QueueDrain()
        {
            if (Interlocked.CompareExchange(ref draining, 1, 0) != 0) return;
            // IVsOutputWindow is an STA COM service; never touch it from Maven's worker threads.
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var output = ServiceProvider.GlobalProvider.GetService(typeof(SVsOutputWindow))
                        as IVsOutputWindow;
                    var panes = new Dictionary<string, IVsOutputWindowPane>(
                        StringComparer.OrdinalIgnoreCase);
                    OutputLine entry;
                    int linesInBatch = 0;
                    while (Pending.TryDequeue(out entry))
                    {
                        if (output == null) continue; // disk log is still authoritative
                        try
                        {
                            IVsOutputWindowPane pane;
                            if (!panes.TryGetValue(entry.Project, out pane))
                            {
                                string hash = HashId(entry.Project);
                                var bytes = Enumerable.Range(0, 16).Select(i =>
                                    Convert.ToByte(hash.Substring(i * 2, 2), 16)).ToArray();
                                var guid = new Guid(bytes);
                                int result = output.GetPane(ref guid, out pane);
                                if (ErrorHandler.Failed(result) || pane == null)
                                {
                                    string caption = "JPM - " + Path.GetFileNameWithoutExtension(entry.Project) +
                                        " [" + hash.Substring(0, 8) + "]";
                                    output.CreatePane(ref guid, caption, 1, 0);
                                    output.GetPane(ref guid, out pane);
                                }
                                panes[entry.Project] = pane;
                            }
                            if (pane == null) continue;
                            if (!string.IsNullOrEmpty(entry.Message))
                                pane.OutputStringThreadSafe(entry.Message);
                            if (entry.Activate) pane.Activate();
                        }
                        catch (Exception ex) { Debug.WriteLine("JPM Output: " + ex.Message); }
                        // Long Maven/Gradle reports must not monopolize the VS UI thread.
                        if (++linesInBatch == 150)
                        {
                            linesInBatch = 0;
                            await Task.Delay(15);
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        }
                    }
                }
                catch (Exception ex) { Debug.WriteLine("JPM Output: " + ex.Message); }
                finally
                {
                    Interlocked.Exchange(ref draining, 0);
                    if (!Pending.IsEmpty) QueueDrain();
                }
            });
        }
    }
}
