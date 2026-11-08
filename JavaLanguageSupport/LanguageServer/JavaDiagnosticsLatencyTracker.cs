using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.Text;
using Newtonsoft.Json.Linq;

namespace JavaLanguageSupport.LanguageServer
{
    /// <summary>
    /// F2J.3: CPS diagnostic latency monitor. Observation only: never changes
    /// document contents, sends RPC notifications, or schedules Java jobs.
    /// Timings use a monotonic clock; a diagnostic without a matching version
    /// is an observation, NOT proof of completion for the most recent edit.
    /// </summary>
    internal static class JavaDiagnosticsLatencyTracker
    {
        private sealed class Document
        {
            internal string Name;
            internal long EditTicks, SentTicks, PublishTicks;
            internal int EditNumber, SentEditNumber, PublishedEditNumber;
            internal int? SentVersion, PublishedVersion;
            internal string SentKind = "-";
            internal int EditCount, Sends, Publications, DiagnosticsCount;
            internal long LastEditToSendMs = -1;
            internal long LastSendToPublishMs = -1;
            internal long LastEditToPublishMs = -1;
            internal long LastForwardMs = -1;
            internal string VersionEvidence = "non disponibile";
        }

        private sealed class BufferRegistration
        {
            private readonly string uri;

            internal BufferRegistration(ITextBuffer buffer, string uri)
            {
                this.uri = uri;
                buffer.Changed += (sender, args) => ObserveEdit(this.uri);
            }
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Document> Documents =
            new Dictionary<string, Document>(StringComparer.OrdinalIgnoreCase);
        private static int totalPublications;
        private static int totalSentChanges;
        private static int totalEdits;
        private static readonly Queue<string> Recent = new Queue<string>();

        // A buffer owns its subscription; split views cannot create duplicates.
        // Only loaded CPS Java documents are registered by the view listener.
        internal static void TrackBuffer(ITextBuffer buffer, string uri)
        {
            if (buffer == null || string.IsNullOrWhiteSpace(uri)) return;
            buffer.Properties.GetOrCreateSingletonProperty(
                () => new BufferRegistration(buffer, uri));
            lock (Gate) GetDocument(uri);
        }

        private static Document GetDocument(string uri)
        {
            Document doc;
            if (!Documents.TryGetValue(uri, out doc))
            {
                // The monitor never stores file contents or full paths in reports.
                if (Documents.Count >= 128)
                {
                    // Discard a document with the oldest observed edit/publication.
                    var oldest = Documents.OrderBy(x => Math.Max(x.Value.EditTicks,
                        x.Value.PublishTicks)).First();
                    Documents.Remove(oldest.Key);
                }
                string name = "(documento Java)";
                try
                {
                    var path = new Uri(uri);
                    if (path.IsFile) name = Path.GetFileName(path.LocalPath);
                }
                catch (UriFormatException) { }
                doc = new Document { Name = name };
                Documents.Add(uri, doc);
            }
            return doc;
        }

        private static long Ms(long a, long b)
        {
            if (a <= 0 || b < a) return -1;
            return (long)((b - a) * 1000.0 / Stopwatch.Frequency);
        }

        private static string ShowMs(long duration) =>
            duration < 0 ? "n/d" : duration.ToString() + " ms";

        private static void ObserveEdit(string uri)
        {
            lock (Gate)
            {
                Document doc = GetDocument(uri);
                doc.EditNumber++;
                doc.EditCount++;
                doc.EditTicks = Stopwatch.GetTimestamp();
                totalEdits++;
                // Do not inherit a previously published measurement on a new edit.
                doc.LastEditToPublishMs = -1;
                doc.VersionEvidence = "attesa di publishDiagnostics";
            }
        }

        // Called after forwarding the event by the VS native LSP client.
        internal static void ObserveOutgoing(string method, JToken parameters, string origin)
        {
            string uri = (string)parameters?.SelectToken("textDocument.uri");
            int? version = (int?)parameters?.SelectToken("textDocument.version");
            ObserveOutgoingCore(method, uri, version, origin);
        }

        // Bridge notifications are anonymous C# objects containing the ENTIRE
        // source. Read only metadata by reflection; NEVER serialize the object,
        // since that would copy Java text on every keystroke and add latency.
        internal static void ObserveBridgeOutgoing(string method, object parameters)
        {
            if (method != "textDocument/didOpen" &&
                method != "textDocument/didChange" &&
                method != "textDocument/didClose") return;
            try
            {
                if (parameters == null) return;
                object textDoc = parameters.GetType().GetProperty("textDocument")
                    ?.GetValue(parameters, null);
                if (textDoc == null) return;
                string uri = textDoc.GetType().GetProperty("uri")
                    ?.GetValue(textDoc, null) as string;
                object versionObject = textDoc.GetType().GetProperty("version")
                    ?.GetValue(textDoc, null);
                int? version = versionObject is int ? (int?)versionObject : null;
                ObserveOutgoingCore(method, uri, version, "CPS-bridge");
            }
            catch (Exception) { /* Diagnostics must never affect RPC. */ }
        }

        private static void ObserveOutgoingCore(string method, string uri,
            int? version, string origin)
        {
            if (method != "textDocument/didOpen" &&
                method != "textDocument/didChange" &&
                method != "textDocument/didClose") return;
            if (string.IsNullOrWhiteSpace(uri)) return;
            lock (Gate)
            {
                Document doc;
                if (!Documents.TryGetValue(uri, out doc)) return;
                if (method == "textDocument/didClose") return;
                doc.SentTicks = Stopwatch.GetTimestamp();
                doc.SentVersion = version;
                doc.SentEditNumber = doc.EditNumber;
                doc.SentKind = origin + "/" +
                    (method == "textDocument/didOpen" ? "didOpen" : "didChange");
                doc.Sends++;
                if (method == "textDocument/didChange") totalSentChanges++;
                doc.LastEditToSendMs = Ms(doc.EditTicks, doc.SentTicks);
            }
        }

        // Timestamp at receipt, BEFORE forwarding the original notification to VS.
        internal static void ObservePublishReceived(JToken parameters)
        {
            string uri = (string)parameters?["uri"];
            if (string.IsNullOrWhiteSpace(uri)) return;
            long now = Stopwatch.GetTimestamp();
            lock (Gate)
            {
                Document doc;
                if (!Documents.TryGetValue(uri, out doc)) return;
                doc.PublishTicks = now;
                doc.PublishedEditNumber = doc.EditNumber;
                doc.PublishedVersion = (int?)parameters?["version"];
                doc.DiagnosticsCount = (parameters?["diagnostics"] as JArray)?.Count ?? -1;
                doc.Publications++;
                totalPublications++;
                doc.LastSendToPublishMs = Ms(doc.SentTicks, now);
                doc.LastEditToPublishMs = Ms(doc.EditTicks, now);

                // Version is optional in LSP publishDiagnostics. Never label an
                // unversioned response as a confirmed result for the latest edit.
                if (!doc.PublishedVersion.HasValue || !doc.SentVersion.HasValue)
                    doc.VersionEvidence = "senza versione confrontabile (solo temporale)";
                else if (doc.PublishedVersion.Value < doc.SentVersion.Value)
                    doc.VersionEvidence = "RISPOSTA OBSOLETA rispetto all'ultimo didChange";
                else if (doc.SentEditNumber != doc.EditNumber)
                    doc.VersionEvidence = "modifiche successive all'ultimo invio osservato";
                else
                    doc.VersionEvidence = "versione >= ultimo invio osservato";
                string entry = doc.Name + ": modifica→invio " + ShowMs(doc.LastEditToSendMs) +
                    ", invio→diagnostica " + ShowMs(doc.LastSendToPublishMs) +
                    ", modifica→diagnostica " + ShowMs(doc.LastEditToPublishMs) +
                    ", diagnostiche=" + doc.DiagnosticsCount +
                    ", versione=" + (doc.PublishedVersion.HasValue ?
                        doc.PublishedVersion.Value.ToString() : "assente") +
                    " (" + doc.VersionEvidence + ")";
                Recent.Enqueue(entry);
                while (Recent.Count > 8) Recent.Dequeue();
            }
        }

        // Measures only the time for the client notification callback to return;
        // it does NOT measure when squiggles are eventually painted on screen.
        internal static void ObservePublishForwarded(JToken parameters, long startTicks)
        {
            string uri = (string)parameters?["uri"];
            if (string.IsNullOrWhiteSpace(uri)) return;
            lock (Gate)
            {
                Document doc;
                if (Documents.TryGetValue(uri, out doc))
                    doc.LastForwardMs = Ms(startTicks, Stopwatch.GetTimestamp());
            }
        }

        internal static void Reset()
        {
            lock (Gate)
            {
                Documents.Clear();
                Recent.Clear();
                totalPublications = totalSentChanges = totalEdits = 0;
            }
        }

        internal static string Report
        {
            get
            {
                lock (Gate)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("Tracciamento passivo dei soli file .java in progetti CPS caricati.");
                    sb.AppendLine("Modifiche osservate=" + totalEdits +
                        "; didChange inoltrati=" + totalSentChanges +
                        "; publishDiagnostics ricevuti=" + totalPublications + ".");
                    foreach (Document doc in Documents.Values.OrderByDescending(x =>
                        Math.Max(x.EditTicks, x.PublishTicks)).Take(5))
                    {
                        bool pending = doc.EditTicks > doc.PublishTicks;
                        long pendingMs = pending ?
                            Ms(doc.EditTicks, Stopwatch.GetTimestamp()) : -1;
                        sb.AppendLine(doc.Name + ": edits=" + doc.EditCount +
                            ", invii=" + doc.Sends + " (ultimo: " + doc.SentKind +
                            "), diagnostiche=" + doc.Publications +
                            ", ultimo edit→invio=" + ShowMs(doc.LastEditToSendMs) +
                            ", ultimo invio→diagnostica=" + ShowMs(doc.LastSendToPublishMs) +
                            ", callback VS=" + ShowMs(doc.LastForwardMs) +
                            (pending ? ", in attesa da " + ShowMs(pendingMs) : "") +
                            "; " + doc.VersionEvidence);
                    }
                    if (Recent.Count != 0)
                    {
                        sb.AppendLine("Ultime notifiche (tempi NON necessariamente causali):");
                        foreach (string entry in Recent) sb.AppendLine("  " + entry);
                    }
                    sb.AppendLine("Nota: callback VS != rendering editor;" +
                        " senza versioni non si puo' attribuire una notifica a un preciso edit.");
                    return sb.ToString().TrimEnd();
                }
            }
        }
    }
}
