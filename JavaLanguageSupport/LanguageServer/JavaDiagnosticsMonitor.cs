using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace JavaLanguageSupport.LanguageServer
{
    /// <summary>
    /// Passive LSP diagnostic monitor. Never creates diagnostic markers,
    /// modifies messages, or initiates a workspace build/reindex.
    /// </summary>
    internal static class JavaDiagnosticsMonitor
    {
        private static readonly object Sync = new object();
        // Diagnostics published by JDT LS are the source of truth for quick fixes.
        // Keep only the most recent snapshot per URI; bounded, memory-only cache.
        private static readonly Dictionary<string, JArray> Published =
            new Dictionary<string, JArray>(StringComparer.OrdinalIgnoreCase);
        // Pack A3: do not reuse diagnostics for an earlier document version.
        // JDT LS may include publishDiagnostics.version; LSP permits it to be absent.
        private static readonly Dictionary<string, int> ClientVersions =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static int invalidations;
        private static int rejectedOldVersions;
        private static int notifications;
        private static int errors;
        private static int warnings;
        private static int info;
        private static string filename = "(nessun documento)";
        private static DateTime lastUtc = DateTime.MinValue;

        // Pack A3: called when the LSP client opens/changes/closes documents.
        // Invalidation is conservative: missing quick-fix diagnostics are safer
        // than applying an outdated fix against a newer editor snapshot.
        internal static void ObserveDocumentNotification(string method, JToken parameters)
        {
            string uri = parameters == null ? null :
                (string)parameters.SelectToken("textDocument.uri");
            if (string.IsNullOrWhiteSpace(uri)) return;
            int? version = (int?)parameters.SelectToken("textDocument.version");
            lock (Sync)
            {
                if (Published.Remove(uri)) invalidations++;
                if (method == "textDocument/didClose")
                    ClientVersions.Remove(uri);
                else if (version.HasValue && version.Value >= 0)
                {
                    int current;
                    if (!ClientVersions.TryGetValue(uri, out current) ||
                        version.Value >= current || method == "textDocument/didOpen")
                        ClientVersions[uri] = version.Value;
                }
            }
        }

        internal static void Reset()
        {
            lock (Sync)
            {
                Published.Clear();
                ClientVersions.Clear();
                notifications = errors = warnings = info = 0;
                invalidations = rejectedOldVersions = 0;
                filename = "(nessun documento)";
                lastUtc = DateTime.MinValue;
            }
        }

        internal static void Observe(JToken parameters)
        {
            var array = parameters == null ? null : parameters["diagnostics"] as JArray;
            if (array == null) return;
            int errorCount = 0, warningCount = 0, infoCount = 0;
            foreach (JToken diagnostic in array)
            {
                int severity = (int?)diagnostic["severity"] ?? 1;
                if (severity == 1) errorCount++;
                else if (severity == 2) warningCount++;
                else infoCount++;
            }
            string file = "(URI mancante)";
            string uri = (string)parameters["uri"];
            Uri parsed;
            if (!string.IsNullOrEmpty(uri) && Uri.TryCreate(uri, UriKind.Absolute, out parsed))
                file = Path.GetFileName(parsed.IsFile ? parsed.LocalPath : parsed.AbsolutePath);
            lock (Sync)
            {
                if (!string.IsNullOrWhiteSpace(uri))
                {
                    // Pack A3: ignore versioned diagnostics predating the most
                    // recent client edit. This affects only our cache; native VS
                    // still receives JDT LS notifications unchanged.
                    int current;
                    int? publishedVersion = (int?)parameters["version"];
                    if (publishedVersion.HasValue &&
                        ClientVersions.TryGetValue(uri, out current) &&
                        publishedVersion.Value < current)
                    {
                        rejectedOldVersions++;
                        return;
                    }
                    if (Published.Count >= 128 && !Published.ContainsKey(uri))
                        Published.Clear();
                    Published[uri] = (JArray)array.DeepClone();
                }
                notifications++;
                errors = errorCount;
                warnings = warningCount;
                info = infoCount;
                filename = file;
                lastUtc = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Diagnostics covering a cursor position, retaining original LSP data/code.
        /// Only those belonging to this document are sent back to JDT LS.
        /// Does not modify the diagnostic notifications forwarded to Visual Studio.
        /// </summary>
        internal static JArray ForPosition(string uri, int line, int character)
        {
            var found = new JArray();
            if (string.IsNullOrWhiteSpace(uri) || line < 0 || character < 0)
                return found;
            lock (Sync)
            {
                JArray diagnostics;
                if (!Published.TryGetValue(uri, out diagnostics)) return found;
                foreach (JToken diagnostic in diagnostics)
                {
                    JToken range = diagnostic["range"];
                    if (range == null) continue;
                    int startLine = (int?)range.SelectToken("start.line") ?? -1;
                    int startChar = (int?)range.SelectToken("start.character") ?? -1;
                    int endLine = (int?)range.SelectToken("end.line") ?? -1;
                    int endChar = (int?)range.SelectToken("end.character") ?? -1;
                    if (startLine < 0 || startChar < 0 || endLine < 0 || endChar < 0)
                        continue;
                    // LSP ranges are end-exclusive. Also tolerate the caret just
                    // after a symbol, as Visual Studio can place it there.
                    bool atStartOrAfter = line > startLine ||
                        (line == startLine && character >= startChar);
                    bool atEndOrBefore = line < endLine ||
                        (line == endLine && character <= endChar + 1);
                    if (atStartOrAfter && atEndOrBefore)
                        found.Add(diagnostic.DeepClone());
                    if (found.Count == 16) break;
                }
            }
            return found;
        }

        internal static string Summary
        {
            get
            {
                lock (Sync)
                {
                    if (notifications == 0)
                        return "nessuna notifica osservata (controllare importazione progetto/JDT LS)";
                    return "notifiche=" + notifications + "; ultima=" + filename +
                        "; errori=" + errors + "; avvisi=" + warnings +
                        "; info=" + info + "; ora=" + lastUtc.ToLocalTime().ToString("HH:mm:ss") +
                        "; invalidazioni=" + invalidations +
                        "; risposte obsolete ignorate=" + rejectedOldVersions +
                        " (contatori riferiti soltanto all'ULTIMA notifica, non all'intero progetto)";
                }
            }
        }
    }
}
