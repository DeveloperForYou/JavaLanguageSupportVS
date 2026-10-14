using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

namespace JavaLanguageSupport.LanguageServer
{
    /// <summary>
    /// Advisory activity monitor. Observed progress/status notifications do NOT
    /// guarantee that JDT LS has completed indexing. Never block the UI thread.
    /// </summary>
    internal static class JavaWorkspaceActivity
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, DateTime> Active =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);
        internal static event EventHandler BecameIdle;
        private static bool serverInitialized;
        private static bool serverStarting;
        private static string lastMessage = "JDT LS non avviato";
        private static DateTime lastUpdateUtc = DateTime.UtcNow;

        internal static void Starting()
        {
            lock (Sync)
            {
                Active.Clear();
                serverInitialized = false;
                serverStarting = true;
                lastMessage = "Avvio JDT LS";
                lastUpdateUtc = DateTime.UtcNow;
            }
        }

        internal static void Initialized()
        {
            lock (Sync)
            {
                serverInitialized = true;
                serverStarting = false;
                lastMessage = "Connessione LSP inizializzata; indice Java non verificato";
                lastUpdateUtc = DateTime.UtcNow;
            }
        }

        internal static void Stopped()
        {
            lock (Sync)
            {
                serverInitialized = false;
                serverStarting = false;
                Active.Clear();
                lastMessage = "JDT LS arrestato";
                lastUpdateUtc = DateTime.UtcNow;
            }
        }

        internal static void Observe(string method, JToken parameters)
        {
            if (!(parameters is JObject)) return;
            bool statusBarUpdate = false;
            bool statusBarBusy = false;
            bool becameIdle = false;
            lock (Sync)
            {
                bool busyBefore = serverStarting || Active.Count != 0;
                DateTime now = DateTime.UtcNow;
                if (method == "$/progress")
                {
                    string token = (string)parameters["token"];
                    JToken value = parameters["value"];
                    string kind = (string)(value == null ? null : value["kind"]);
                    if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(kind)) return;
                    if (kind == "begin" || kind == "report") Active[token] = now;
                    else if (kind == "end") Active.Remove(token);
                    else return;
                    statusBarUpdate = kind == "begin" || kind == "end";
                    string title = (string)(value == null ? null : value["title"]);
                    string message = (string)(value == null ? null : value["message"]);
                    lastMessage = !string.IsNullOrWhiteSpace(message) ? message :
                        (!string.IsNullOrWhiteSpace(title) ? title : "Attivita' Java LSP");
                }
                else if (method == "language/status")
                {
                    string type = (string)parameters["type"];
                    string message = (string)parameters["message"];
                    if (!string.IsNullOrWhiteSpace(message)) lastMessage = message;
                    // JDT LS versions differ in status names; don't infer readiness
                    // from an arbitrary message. LSP initialize is tracked separately.
                    if (string.Equals(type, "Starting", StringComparison.OrdinalIgnoreCase) || type == "0")
                        serverStarting = true;
                    if (string.Equals(type, "Started", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(type, "ServiceReady", StringComparison.OrdinalIgnoreCase) || type == "1")
                        serverStarting = false;
                    statusBarUpdate = type == "0" || type == "1" ||
                        string.Equals(type, "Starting", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(type, "Started", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(type, "ServiceReady", StringComparison.OrdinalIgnoreCase);
                }
                else return;
                lastUpdateUtc = now;
                statusBarBusy = serverStarting || Active.Count != 0;
                becameIdle = busyBefore && !statusBarBusy;
            }
            if (statusBarUpdate) UpdateStatusBar(statusBarBusy);
            if (becameIdle)
            {
                var handler = BecameIdle;
                if (handler != null)
                {
                    try { handler(null, EventArgs.Empty); }
                    catch (Exception) { /* Observers cannot block JDT LS. */ }
                }
            }
        }

        private static void UpdateStatusBar(bool busy)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var bar = Package.GetGlobalService(typeof(SVsStatusbar)) as IVsStatusbar;
                if (bar != null)
                    bar.SetText(busy ? "Java: aggiornamento workspace JDT LS in corso..." :
                        "Java: elaborazione LSP terminata (indice da verificare)");
            }).FileAndForget("JavaLanguageSupport/WorkspaceActivity");
        }

        internal static bool IsBusy
        {
            get
            {
                lock (Sync)
                {
                    // Progress tokens can be abandoned by a crashed server. Avoid
                    // an indefinitely "busy" UI by expiring old observations.
                    var expired = new List<string>();
                    foreach (var pair in Active)
                        if ((DateTime.UtcNow - pair.Value).TotalSeconds > 90)
                            expired.Add(pair.Key);
                    foreach (string token in expired) Active.Remove(token);
                    if (serverStarting &&
                        (DateTime.UtcNow - lastUpdateUtc).TotalSeconds > 90)
                        serverStarting = false;
                    return serverStarting || Active.Count != 0;
                }
            }
        }

        internal static string Summary
        {
            get
            {
                bool busy = IsBusy;
                lock (Sync)
                {
                    string state = !serverInitialized ? "non inizializzato" :
                        busy ? "attivita' LSP osservata" : "nessuna attivita' osservata";
                    return state + "; " + lastMessage +
                        "; ultimo segnale: " + lastUpdateUtc.ToLocalTime().ToString("HH:mm:ss") +
                        ". Nota: non e' una conferma del completamento dell'indicizzazione.";
                }
            }
        }

        internal static int[] ImplementationRetryDelays()
        {
            return IsBusy ? new[] { 350, 900, 1700 } : new[] { 300, 900 };
        }

        internal static async Task WaitIfBusyAsync(int maxMilliseconds, CancellationToken token)
        {
            int waited = 0;
            while (waited < maxMilliseconds && IsBusy)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(200, token).ConfigureAwait(false);
                waited += 200;
            }
        }
    }
}
