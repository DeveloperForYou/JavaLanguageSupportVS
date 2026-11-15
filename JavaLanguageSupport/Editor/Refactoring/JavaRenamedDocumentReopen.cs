// U4.B.1: restore CPS/RDT and LSP document identity after moving a Java file.
// Closing/reopening a clean, committed tab causes VS to resolve its new CPS itemId
// and causes the Java language client to subscribe the new document URI.
// The shared global Undo manager remains the sole Undo/Redo command target.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace JavaLanguageSupport.Editor.Refactoring
{
    internal static class JavaRenamedDocumentReopen
    {
        private sealed class Entry
        {
            internal Document Document;
            internal string From;
            internal string To;
            internal bool WasActive;
        }

        // Must be called on the UI thread, AFTER the linked Undo transaction,
        // file saves have completed; JDT LS notification may follow.
        // This method performs no disk writes and never rolls back a committed rename.
        internal static void Refresh(IEnumerable<KeyValuePair<string, string>> moves,
            Action<string> report)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                RefreshCore(moves, report);
            }
            catch (Exception ex)
            {
                Report(report, "Schede Java: aggiornamento dopo rinomina non riuscito: " +
                    ex.Message + ". Se necessario chiudi e riapri il sorgente.");
                Trace.WriteLine("[Java U4.B.1] Refresh documenti fallito: " + ex);
            }
        }

        private static void RefreshCore(IEnumerable<KeyValuePair<string, string>> moves,
            Action<string> report)
        {
            if (moves == null) return;
            var destinations = new Dictionary<string, KeyValuePair<string, string>>(
                StringComparer.OrdinalIgnoreCase);
            var sources = new Dictionary<string, KeyValuePair<string, string>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var move in moves)
            {
                if (string.IsNullOrWhiteSpace(move.Key) ||
                    string.IsNullOrWhiteSpace(move.Value)) return;
                string from = Path.GetFullPath(move.Key);
                string to = Path.GetFullPath(move.Value);
                if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(Path.GetExtension(to), ".java",
                    StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(to) || File.Exists(from))
                {
                    Report(report, "Schede Java: i percorsi non sono ancora coerenti; " +
                        "aggiornamento automatico non eseguito.");
                    return;
                }
                if (destinations.ContainsKey(to) || sources.ContainsKey(from))
                    throw new IOException("Percorsi di rinomina duplicati.");
                var pair = new KeyValuePair<string, string>(from, to);
                destinations.Add(to, pair);
                sources.Add(from, pair);
            }
            if (destinations.Count == 0) return;

            var dte = Package.GetGlobalService(typeof(SDTE)) as DTE;
            if (dte == null || dte.Documents == null || dte.ItemOperations == null)
                throw new InvalidOperationException("Servizi DTE non disponibili.");

            Document active = null;
            try { active = dte.ActiveDocument; }
            catch (Exception) { /* Tool window or no active document. */ }

            // Preflight all open, moved tabs. Never close a dirty tab, even though
            // the rename itself has already been committed and saved.
            var selected = new List<Entry>();
            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Document document in dte.Documents)
            {
                string path;
                try { path = Path.GetFullPath(document.FullName); }
                catch (Exception) { continue; }
                KeyValuePair<string, string> rename;
                if (!destinations.TryGetValue(path, out rename) &&
                    !sources.TryGetValue(path, out rename)) continue;
                if (!matched.Add(rename.Value))
                {
                    Report(report, "Schede Java: più finestre per lo stesso sorgente; " +
                        "aggiornamento automatico non eseguito.");
                    return;
                }
                bool saved;
                try { saved = document.Saved; }
                catch (Exception) { saved = false; }
                if (!saved || !File.Exists(rename.Value) || File.Exists(rename.Key))
                {
                    Report(report, "Schede Java: una scheda ha modifiche non salvate; " +
                        "aggiornamento automatico non eseguito.");
                    return;
                }
                selected.Add(new Entry {
                    Document = document,
                    From = rename.Key,
                    To = rename.Value,
                    WasActive = ReferenceEquals(document, active)
                });
            }
            if (selected.Count == 0) return;
            if (selected.Count > 128)
            {
                Report(report, "Schede Java: troppe schede coinvolte; " +
                    "aggiornamento automatico non eseguito.");
                return;
            }

            var closed = new List<Entry>();
            foreach (Entry entry in selected)
            {
                // Recheck immediately before closing, so no user edit is discarded.
                if (!entry.Document.Saved)
                {
                    Report(report, "Schede Java: un documento è diventato non salvato; " +
                        "interruzione dell'aggiornamento.");
                    break;
                }
                try
                {
                    entry.Document.Close(vsSaveChanges.vsSaveChangesNo);
                    closed.Add(entry);
                }
                catch (Exception ex)
                {
                    Report(report, "Schede Java: chiusura non riuscita per " +
                        Path.GetFileName(entry.To) + ": " + ex.Message);
                    break;
                }
            }

            EnvDTE.Window activeReopened = null;
            var reopened = 0;
            foreach (Entry entry in closed)
            {
                try
                {
                    if (!File.Exists(entry.To))
                        throw new FileNotFoundException("Sorgente non trovato", entry.To);
                    var window = dte.ItemOperations.OpenFile(entry.To,
                        EnvDTE.Constants.vsViewKindTextView);
                    if (entry.WasActive) activeReopened = window;
                    reopened++;
                }
                catch (Exception ex)
                {
                    Report(report, "Schede Java: riapertura automatica fallita per " +
                        Path.GetFileName(entry.To) + ". Apri il file da Esplora soluzioni.");
                    Trace.WriteLine("[Java U4.B.1] OpenFile(" + entry.To + "): " + ex);
                }
            }
            try
            {
                if (activeReopened != null) activeReopened.Activate();
                else if (active != null && !selected.Any(e => e.WasActive))
                    active.Activate();
            }
            catch (Exception) { /* Focus best effort; no effect on commit. */ }
            Trace.WriteLine("[Java U4.B.1] Ricollegate " + reopened + "/" +
                selected.Count + " schede dopo rinomina Java.");
            if (reopened != selected.Count)
                Report(report, "Schede Java: aggiornamento parziale; " +
                    "riapri manualmente le schede non ripristinate.");
        }

        private static void Report(Action<string> report, string message)
        {
            Trace.WriteLine("[Java U4.B.1] " + message);
            if (report == null) return;
            try { report(message); }
            catch (Exception) { /* Reporting cannot roll back a committed rename. */ }
        }
    }
}
