using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using System.Linq;

namespace JavaLanguageSupport.LanguageServer
{
    /// <summary>
    /// Stato dell'istanza LSP visibile dal comando Strumenti > Java: Stato IntelliSense.
    /// Tutte le scritture (compreso stderr di JDT LS) sono serializzate.
    /// Non registra stdout: contiene i messaggi JSON-RPC del protocollo.
    /// </summary>
    internal static class JavaLanguageStatus
    {
        private static readonly object Sync = new object();
        private static string state = "Non avviato (apri un file .java)";
        private static string lastError = "";
        private static string logPath = "";
        private static string workspacePath = "";
        private static int? processId;
        private static string initializationSummary = "Initialize non intercettato";
        private static string codeActionSummary = "Nessuna Code Action registrata";
        private static string executeCommandSummary = "Nessun comando LSP eseguito";
        private static readonly System.Collections.Generic.Dictionary<string, int> LspRequests =
            new System.Collections.Generic.Dictionary<string, int>();
        private static readonly System.Collections.Generic.Dictionary<string, string> LspLast =
            new System.Collections.Generic.Dictionary<string, string>();

        internal static void Starting(string javaExe, string launcher, string workspace)
        {
            lock (Sync)
            {
                string logFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "JavaLanguageSupportVS", "logs");
                Directory.CreateDirectory(logFolder);
                logPath = Path.Combine(logFolder, "jdtls-" +
                    Process.GetCurrentProcess().Id + "-" +
                    DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".log");
                workspacePath = workspace;
                processId = null;
                lastError = "";
                LspRequests.Clear();
                LspLast.Clear();
                initializationSummary = "Initialize non intercettato";
                codeActionSummary = "Nessuna Code Action registrata";
                executeCommandSummary = "Nessun comando LSP eseguito";
                state = "Avvio JDT LS";
                WriteInternal("JDT LS startup: " + DateTime.Now.ToString("O"));
                WriteInternal("Java: " + javaExe);
                WriteInternal("Launcher: " + launcher);
                WriteInternal("Dati JDT LS: " + workspace);
            }
        }

        internal static void Started(int pid)
        {
            lock (Sync)
            {
                processId = pid;
                state = "Processo avviato; attesa handshake LSP";
                WriteInternal("PID JDT LS: " + pid);
            }
        }

        internal static void Initialized()
        {
            lock (Sync)
            {
                state = "LSP inizializzato (indicizzazione progetti eventualmente ancora in corso)";
                WriteInternal("Handshake LSP completato.");
            }
        }

        internal static void Failed(string error)
        {
            lock (Sync)
            {
                lastError = error ?? "Errore sconosciuto";
                state = "Errore";
                WriteInternal("ERRORE: " + lastError);
            }
        }

        internal static void Exited(int code)
        {
            lock (Sync)
            {
                state = "JDT LS terminato (exit code " + code + ")";
                WriteInternal(state);
            }
        }

        internal static void Stopped()
        {
            lock (Sync)
            {
                state = "Arrestato";
                WriteInternal(state);
            }
        }

        internal static void LogStderr(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            lock (Sync) WriteInternal("[stderr] " + line);
            Trace.WriteLine("[JDT LS] " + line);
        }

        internal static void LspRequest(string method)
        {
            lock (Sync)
            {
                int count;
                LspRequests.TryGetValue(method, out count);
                LspRequests[method] = count + 1;
            }
        }

        internal static void LspResponse(string method, int items,
            bool hasDocumentation, bool hasAdditionalTextEdits)
        {
            lock (Sync)
            {
                LspLast[method] = "OK; elementi=" + items
                    + "; documentazione=" + hasDocumentation
                    + "; additionalTextEdits=" + hasAdditionalTextEdits;
            }
        }

        internal static void LspFailure(string method, string exceptionType)
        {
            lock (Sync) LspLast[method] = "ERRORE " + exceptionType;
        }


        // LSP initialize: diagnostica del contratto client/server, senza alterarlo.
        internal static void LspInitialize(JToken parameters, JToken result)
        {
            JToken caps = result == null ? null : result["capabilities"];
            JToken client = parameters == null ? null : parameters["capabilities"];
            string root = parameters == null ? null : (string)parameters["rootUri"];
            string info = "RootUri=" + (string.IsNullOrEmpty(root) ? "non impostata" : "presente")
                + "; client.hover=" + Declared(client == null ? null : client.SelectToken("textDocument.hover"))
                + "; server.hoverProvider=" + Declared(caps == null ? null : caps["hoverProvider"])
                + "; server.completionProvider=" + Declared(caps == null ? null : caps["completionProvider"])
                + "; server.resolveProvider=" + Declared(caps == null ? null : caps.SelectToken("completionProvider.resolveProvider"))
                + "; server.codeActionProvider=" + Declared(caps == null ? null : caps["codeActionProvider"]);
            lock (Sync) initializationSummary = info;
        }

        private static string Declared(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return "assente";
            if (token.Type == JTokenType.Boolean) return token.Value<bool>() ? "true" : "false";
            return "presente";
        }

        internal static void LspCodeActions(JArray actions)
        {
            if (actions == null) return;
            var entries = actions.OfType<JObject>().Take(8).Select(action =>
            {
                string title = (string)action["title"] ?? "(senza titolo)";
                title = title.Replace('\r', ' ').Replace('\n', ' ');
                if (title.Length > 90) title = title.Substring(0, 90) + "...";
                bool edit = action["edit"] != null;
                bool command = action["command"] != null;
                return title + " [kind=" + ((string)action["kind"] ?? "n/d")
                    + ", edit=" + edit + ", command=" + command + "]";
            });
            lock (Sync) codeActionSummary = "Totale=" + actions.Count + "; campione: "
                + string.Join(" | ", entries);
        }

        internal static void LspExecuteCommand(JToken parameters)
        {
            string name = parameters == null ? "?" : (string)parameters["command"];
            lock (Sync) executeCommandSummary = string.IsNullOrWhiteSpace(name) ? "(senza nome)" : name;
        }

        internal static string GetReport()
        {
            lock (Sync)
            {
                var result = new StringBuilder();
                result.AppendLine("========== Java IntelliSense / JDT LS ==========");
                result.AppendLine("Stato: " + state);
                result.AppendLine("PID Java: " + (processId.HasValue ? processId.Value.ToString() : "-") );
                result.AppendLine("Workspace dati JDT LS: " + (workspacePath.Length > 0 ? workspacePath : "-"));
                result.AppendLine("Ultimo errore: " + (lastError.Length > 0 ? lastError : "-") );
                result.AppendLine("Log di avvio/stderr: " + (logPath.Length > 0 ? logPath : "-"));
                result.AppendLine("JAVA_HOME: " + (Environment.GetEnvironmentVariable("JAVA_HOME") ?? "<non impostata>"));
                result.AppendLine("JDTLS_HOME: " + (Environment.GetEnvironmentVariable("JDTLS_HOME") ?? "<non impostata>"));
                result.AppendLine("Aprire una CARTELLA Java con File > Apri > Cartella, poi un file .java.");
                result.AppendLine("La sola colorazione non conferma che LSP sia connesso.");
                result.AppendLine("RPC per Quick Info e import: " + (JavaLspRpc.Connected ? "collegato" : "non collegato"));
                result.AppendLine("Attivita' workspace JDT LS: " + JavaWorkspaceActivity.Summary);
                result.AppendLine("--- Capabilities negoziate LSP ---");
                result.AppendLine(initializationSummary);
                result.AppendLine("--- Richieste LSP osservate (da apertura ultimo server) ---");
                foreach (string method in new[] { "textDocument/completion", "completionItem/resolve",
                    "textDocument/hover", "textDocument/codeAction", "workspace/executeCommand",
                    "Java import/applyEdit" })
                {
                    int count;
                    LspRequests.TryGetValue(method, out count);
                    string last;
                    if (!LspLast.TryGetValue(method, out last)) last = "nessuna risposta osservata";
                    result.AppendLine(method + ": richieste=" + count + ", ultimo=" + last);
                }
                result.AppendLine("--- Azioni rapide ricevute (solo titoli, in memoria) ---");
                result.AppendLine(codeActionSummary);
                result.AppendLine("Ultimo workspace/executeCommand: " + executeCommandSummary);
                result.AppendLine("NOTA: completionItem/resolve puo' restare a zero se non e' stato selezionato un suggerimento.");
                result.AppendLine("NOTA: hover a zero indica solo che non e' stata osservata una richiesta hover.");
                return result.ToString();
            }
        }

        private static void WriteInternal(string line)
        {
            // La diagnostica non deve mai interrompere un language server funzionante.
            if (string.IsNullOrEmpty(logPath)) return;
            try { File.AppendAllText(logPath, line + Environment.NewLine, Encoding.UTF8); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
