// U3.C - Java CPS package rename: optional IDE-level Undo/Redo bridge.
// Only eligible package operations are handled. Other commands stay native.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Threading;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.TextManager.Interop;
using EnvDTE;

namespace JavaLanguageSupport.ProjectSystem.Packages
{
    internal static class JavaPackageGlobalUndo
    {
        private sealed class Entry
        {
            internal ITextUndoHistory History;
            internal ITextUndoTransaction Transaction;
            internal ITextUndoPrimitive Primitive; // U3C6: identita' del file-system undo
            internal IOleUndoManager NativeUndo; // U3C7: source buffer's COM undo manager
            internal string Marker;
            internal Func<bool> IsApplied;
            internal string Caption;
            // U3C11 detached package history: non dipende dal buffer chiuso.
            internal string AnchorPath;
            internal string OriginalAnchorPath;
            internal bool AnchorClosed;
            internal Func<bool, bool> CanDetached;
            internal Action<bool> ReplayDetached;
        }

        private const int MaxEntries = 32;
        private static readonly List<Entry> Entries = new List<Entry>();
        private static bool executing;
        // U3C.3 native focus refresh: histories can change through the editor,
        // not just through the package priority command target. Observe them.
        private static readonly HashSet<ITextUndoHistory> ObservedHistories =
            new HashSet<ITextUndoHistory>();
        private static bool commandUiRefreshQueued;

        // Called AFTER the native VS linked transaction has been completed.
        // Never fabricate a second undo history: we must use the actual history
        // that owns PackageRenameUndoPrimitive and its text edits.
        internal static void Remember(ITextUndoHistory history, ITextBuffer anchorBuffer,
            ITextUndoTransaction completedTransaction, ITextUndoPrimitive primitive,
            string marker, Func<bool> isApplied, string oldPackage, string newPackage,
            string anchorPath, string originalAnchorPath,
            Func<bool, bool> canDetached, Action<bool> replayDetached)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // U3C.4: intercettare anche i return silenziosi di Remember.
            JavaLanguageStatus.LogLifecycle("U3C5 Remember: inizio; " +
                "historyNull=" + (history == null) +
                "; transactionNull=" + (completedTransaction == null) +
                "; primitiveNull=" + (primitive == null));
            if (history == null || completedTransaction == null ||
                primitive == null || isApplied == null || string.IsNullOrEmpty(marker) ||
                string.IsNullOrWhiteSpace(anchorPath) ||
                string.IsNullOrWhiteSpace(originalAnchorPath) || canDetached == null ||
                replayDetached == null)
            {
                JavaLanguageStatus.LogLifecycle("U3C5 Remember: parametri mancanti");
                return;
            }
            // U3C5: the transaction object was returned by CreateTransaction.
            // Accessing LastUndoTransaction on this VS2026 history throws
            // NotSupportedException; never use it for registering the rename.
            ITextUndoTransaction top = completedTransaction;
            bool containsPrimitive = ReferenceEquals(primitive.Parent, top);
            if (!containsPrimitive)
            {
                try
                {
                    containsPrimitive = top.UndoPrimitives != null &&
                        top.UndoPrimitives.Any(item => ReferenceEquals(item, primitive));
                }
                catch (NotSupportedException) { /* Parent already checked. */ }
            }
            string managerStatus;
            IOleUndoManager nativeUndo = GetNativeUndoManager(anchorBuffer,
                out managerStatus);
            JavaLanguageStatus.LogLifecycle("U3C7 COM manager: " + managerStatus);
            bool isTop = IsTopTransaction(nativeUndo, marker,
                false, diagnose: true);
            if (!history.CanUndo || !isApplied() || !containsPrimitive || !isTop)
            {
                // U3C.2: keep the safety check. Diagnose WHY the native command
                // is unavailable; do not relax identity checks and undo other edits.
                JavaLanguageStatus.LogLifecycle("U3C.2 Undo registrazione rifiutata: " +
                    "top=" + (top != null) + "; canUndo=" + history.CanUndo +
                    "; primitivePresente=" + containsPrimitive +
                    "; topCorrente=" + isTop);
                return;
            }
            // A new refactoring invalidates the global redo branch, just as a
            // new native edit invalidates redo in a conventional undo stack.
            Entries.RemoveAll(e => !SafeApplied(e));
            Entries.Add(new Entry {
                History = history, Transaction = top, Primitive = primitive,
                NativeUndo = nativeUndo, Marker = marker, IsApplied = isApplied,
                Caption = oldPackage + " -> " + newPackage,
                AnchorPath = System.IO.Path.GetFullPath(anchorPath),
                OriginalAnchorPath = System.IO.Path.GetFullPath(originalAnchorPath),
                CanDetached = canDetached, ReplayDetached = replayDetached
            });
            if (Entries.Count > MaxEntries) Entries.RemoveAt(0);
            WatchHistory(history);
            JavaLanguageStatus.LogLifecycle("U3C.2 Undo registrato: " +
                oldPackage + " -> " + newPackage);
            RefreshNativeCommands();
        }

        // U3C11: l'evento Closing identifica la perdita dell'originale
        // undo buffer; l'eventuale nuova scheda NON eredita la sua cronologia.
        internal static void DocumentClosing(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrWhiteSpace(path)) return;
            string full;
            try { full = System.IO.Path.GetFullPath(path); }
            catch (Exception) { return; }
            bool changed = false;
            foreach (Entry entry in Entries)
            {
                if (!entry.AnchorClosed &&
                    (string.Equals(entry.AnchorPath, full, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(entry.OriginalAnchorPath, full,
                         StringComparison.OrdinalIgnoreCase)))
                {
                    entry.AnchorClosed = true;
                    changed = true;
                    JavaLanguageStatus.LogLifecycle("U3C11 anchor chiuso: " +
                        System.IO.Path.GetFileName(full) + "; fallback CPS disponibile");
                }
            }
            if (changed) RefreshNativeCommands();
        }

        private static void WatchHistory(ITextUndoHistory history)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!ObservedHistories.Add(history)) return;
            history.UndoRedoHappened += OnNativeUndoRedo;
            history.UndoTransactionCompleted += OnNativeTransactionCompleted;
        }

        private static void OnNativeUndoRedo(object sender, TextUndoRedoEventArgs e)
        {
            // Covers Undo from the original anchor editor (not routed through
            // our priority target), plus global commands and ordinary text Undo.
            if (ThreadHelper.CheckAccess()) RefreshNativeCommands();
        }

        private static void OnNativeTransactionCompleted(object sender,
            TextUndoTransactionCompletedEventArgs e)
        {
            if (ThreadHelper.CheckAccess()) RefreshNativeCommands();
        }

        // U3C7: VS2026 can expose an ITextUndoHistory implementation whose
        // LastUndoTransaction, UndoStack and UndoDescription all throw
        // NotSupportedException. Read the top operation from the *native* undo
        // manager of the very same Java text buffer. Never search deeper than
        // the top entry. A missing or differing marker means no interception.
        private static IOleUndoManager GetNativeUndoManager(ITextBuffer buffer,
            out string status)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            status = "anchor mancante";
            if (buffer == null) return null;
            try
            {
                var componentModel = Package.GetGlobalService(typeof(SComponentModel))
                    as IComponentModel;
                var adapters = componentModel == null ? null :
                    componentModel.GetService<IVsEditorAdaptersFactoryService>();
                if (adapters == null)
                {
                    status = "IVsEditorAdaptersFactoryService non disponibile";
                    return null;
                }
                var nativeBuffer = adapters.GetBufferAdapter(buffer);
                if (nativeBuffer == null)
                {
                    status = "GetBufferAdapter: nessun buffer nativo";
                    return null;
                }
                IOleUndoManager manager;
                int hr = nativeBuffer.GetUndoManager(out manager);
                if (ErrorHandler.Failed(hr) || manager == null)
                {
                    status = "GetUndoManager HRESULT=0x" + hr.ToString("X8") +
                        "; null=" + (manager == null);
                    return null;
                }
                status = "IOleUndoManager disponibile";
                return manager;
            }
            catch (Exception ex)
            {
                status = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        private static bool IsTopTransaction(IOleUndoManager nativeUndo,
            string marker, bool redo, bool diagnose = false)
        {
            bool valid = false;
            string observation = "IOleUndoManager non disponibile";
            if (nativeUndo != null && !string.IsNullOrWhiteSpace(marker))
            {
                try
                {
                    string description;
                    if (redo) nativeUndo.GetLastRedoDescription(out description);
                    else nativeUndo.GetLastUndoDescription(out description);
                    valid = !string.IsNullOrEmpty(description) &&
                        description.IndexOf("[" + marker + "]",
                            StringComparison.Ordinal) >= 0;
                    // Do not log complete paths or document contents.
                    observation = "descrizioneTop=" +
                        (string.IsNullOrEmpty(description) ? "<vuota>" :
                            description.Substring(0, Math.Min(description.Length, 120))) +
                        "; markerPresente=" + valid;
                }
                catch (Exception ex)
                {
                    observation = ex.GetType().Name + ": " + ex.Message;
                }
            }
            if (diagnose)
                JavaLanguageStatus.LogLifecycle("U3C7 COM native top " +
                    (redo ? "Redo" : "Undo") + ": valido=" + valid +
                    "; " + observation);
            return valid;
        }

        // U3C8 routing: when another editor is active, the source-buffer COM
        // manager can tell whether its own top operation is the SAME package
        // rename. Never take over unrelated local text Undo/Redo.
        internal static bool IsNativeTopForBuffer(ITextBuffer buffer, string marker,
            bool redo)
        {
            string status;
            var manager = GetNativeUndoManager(buffer, out status);
            return IsTopTransaction(manager, marker, redo);
        }

        // U3C12 native command caption: text for VSStd97 MultiLevelUndo/Redo.
        // Candidate returns the detached entry too, even after the anchor closes.
        internal static string CandidateCaption(bool redo)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Entry entry = Candidate(redo);
            if (entry == null) return null;
            return (redo ? "Ripeti rinomina package: " : "Annulla rinomina package: ") +
                entry.Caption;
        }

        // No extra ITextUndoHistory stack calls: they throw NotSupportedException
        // in this editor. Keep the diagnostic light enough for QueryStatus.
        internal static string ExplainCandidate(bool redo)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Entries.Count == 0) return "registrazioni=0";
            Entry last = Entries[Entries.Count - 1];
            bool applied = SafeApplied(last);
            if (last.AnchorClosed)
            {
                bool can = false;
                string error = "";
                try { can = last.CanDetached != null && last.CanDetached(redo); }
                catch (Exception ex) { error = ex.GetType().Name; }
                return "registrazioni=" + Entries.Count + "; detached=True; applied=" +
                    applied + "; canDetached=" + can +
                    (error.Length == 0 ? "" : "; err=" + error);
            }
            bool nativeTop = false;
            bool historyCan = false;
            string reason = "";
            try
            {
                historyCan = redo ? last.History.CanRedo : last.History.CanUndo;
                nativeTop = IsTopTransaction(last.NativeUndo, last.Marker, redo);
            }
            catch (Exception ex) { reason = ex.GetType().Name; }
            return "registrazioni=" + Entries.Count + "; detached=False; applied=" +
                applied + "; historyCan=" + historyCan + "; nativeTop=" +
                nativeTop + (reason.Length == 0 ? "" : "; err=" + reason);
        }

        // U3C13 local COM HRESULT: distinguish empty OLE undo (E_FAIL) from
        // disabled/failed managers. CanUndo true alone is NOT a reason to
        // override this history, even when the document is saved.
        internal static bool IsTrackedAnchorHistory(ITextUndoHistory history)
        {
            return history != null && Entries.Any(x => !x.AnchorClosed &&
                ReferenceEquals(x.History, history));
        }

        internal static string DescribeBufferTop(ITextBuffer buffer, bool redo)
        {
            string status;
            IOleUndoManager manager = GetNativeUndoManager(buffer, out status);
            if (manager == null) return "noCOM:" + status;
            try
            {
                string description;
                if (redo) manager.GetLastRedoDescription(out description);
                else manager.GetLastUndoDescription(out description);
                return string.IsNullOrWhiteSpace(description) ? "<empty>" :
                    description.Substring(0, Math.Min(description.Length, 96));
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                string code = unchecked((uint)ex.ErrorCode).ToString("X8");
                return "COM:0x" + code +
                    (ex.ErrorCode == unchecked((int)0x80004005) ? "(E_FAIL)" :
                    ex.ErrorCode == unchecked((int)0x8000FFFF) ? "(E_UNEXPECTED)" : "");
            }
            catch (Exception ex) { return "COM:" + ex.GetType().Name; }
        }

        // U3C14: OLE stack empty override (only for a clean, unrelated Java tab).
        // An editor may report CanUndo/CanRedo=true on its MEF history while its
        // native buffer undo manager reports E_FAIL for BOTH stacks. E_FAIL means
        // the queried OLE stack has no top-level undo unit. Any other result,
        // including a nonempty description, E_UNEXPECTED, unsupported COM, or a
        // missing manager, must preserve the editor's local Undo precedence.
        internal static bool AreNativeStacksEmpty(ITextBuffer buffer)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string status;
            IOleUndoManager manager = GetNativeUndoManager(buffer, out status);
            if (manager == null) return false;
            const int EmptyOleStack = unchecked((int)0x80004005);
            bool undoEmpty = false, redoEmpty = false;
            try
            {
                string description;
                manager.GetLastUndoDescription(out description);
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                undoEmpty = ex.ErrorCode == EmptyOleStack;
            }
            catch (Exception) { return false; }
            if (!undoEmpty) return false;
            try
            {
                string description;
                manager.GetLastRedoDescription(out description);
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                redoEmpty = ex.ErrorCode == EmptyOleStack;
            }
            catch (Exception) { return false; }
            return redoEmpty;
        }

        // Keep the priority command target cheap for unrelated commands.
        internal static bool HasTrackedRenames { get { return Entries.Count != 0; } }

        private static bool SafeApplied(Entry entry)
        {
            try { return entry.IsApplied(); }
            catch (Exception) { return false; }
        }

        // A valid global history has all applied operations followed by all
        // undone operations. If native editor actions made it incoherent, the
        // bridge refuses to guess and leaves native Undo/Redo in control.
        private static Entry Candidate(bool redo)
        {
            if (executing || Entries.Count == 0) return null;
            bool seenUndone = false;
            int firstUndone = -1, lastApplied = -1;
            for (int i = 0; i < Entries.Count; ++i)
            {
                if (SafeApplied(Entries[i]))
                {
                    if (seenUndone) return null;
                    lastApplied = i;
                }
                else
                {
                    seenUndone = true;
                    if (firstUndone < 0) firstUndone = i;
                }
            }
            int index = redo ? firstUndone : lastApplied;
            if (index < 0) return null;
            Entry entry = Entries[index];
            // U3C11: non interrogare una history potenzialmente disposed.
            if (entry.AnchorClosed)
            {
                try { return entry.CanDetached != null && entry.CanDetached(redo)
                    ? entry : null; }
                catch (Exception) { return null; }
            }
            try
            {
                // Do not undo unrelated text edits that appeared on top of
                // the package transaction in its original anchor buffer.
                try
                {
                    if (entry.History.CurrentTransaction != null) return null;
                }
                catch (NotSupportedException) { /* DTE scope checked on execute. */ }
                bool can = redo ? entry.History.CanRedo : entry.History.CanUndo;
                return can && IsTopTransaction(entry.NativeUndo,
                    entry.Marker, redo) ? entry : null;
            }
            catch (Exception) { return null; }
        }

        // Expose the validated package marker to the priority command target.
        // Candidate checks the package anchor's real COM undo top first.
        internal static string CandidateMarker(bool redo)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Entry entry = Candidate(redo);
            return entry == null ? null : entry.Marker;
        }

        private static string lastAvailabilityDiagnostic;
        internal static bool CanExecute(bool redo)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            bool available = Candidate(redo) != null;
            if (!available && Entries.Count != 0)
            {
                Entry entry = Entries[Entries.Count - 1];
                bool topMatch = false;
                bool can = false;
                try
                {
                    can = redo ? entry.History.CanRedo : entry.History.CanUndo;
                    topMatch = IsTopTransaction(entry.NativeUndo,
                        entry.Marker, redo);
                }
                catch (Exception) { /* diagnostic only */ }
                string state = (redo ? "Redo" : "Undo") +
                    " non disponibile: can=" + can + "; topMatch=" + topMatch +
                    "; registrazioni=" + Entries.Count;
                if (!string.Equals(state, lastAvailabilityDiagnostic, StringComparison.Ordinal))
                {
                    lastAvailabilityDiagnostic = state;
                    JavaLanguageStatus.LogLifecycle("U3C.2 " + state);
                }
            }
            else if (available)
            {
                lastAvailabilityDiagnostic = null;
            }
            return available;
        }

        internal static bool Execute(bool redo)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var target = Candidate(redo);
            if (target == null) return false;
            // Do not run while Visual Studio has a linked undo scope open.
            var dte = Package.GetGlobalService(typeof(SDTE)) as DTE;
            if (dte != null && dte.UndoContext != null && dte.UndoContext.IsOpen)
                return false;
            executing = true;
            try
            {
                if (target.AnchorClosed)
                {
                    JavaLanguageStatus.LogLifecycle("U3C11 fallback: " +
                        (redo ? "Redo" : "Undo") + "; " + target.Caption);
                    target.ReplayDetached(redo);
                }
                else if (redo) target.History.Redo(1);
                else target.History.Undo(1);
                Trace.WriteLine("[Java U3.C] " + (redo ? "Ripeti " : "Annulla ") + target.Caption);
                return true;
            }
            finally
            {
                executing = false;
                RefreshNativeCommands();
            }
        }

        // Ask VS to requery the native Edit.Undo/Edit.Redo arrows only
        // AFTER the editor/solution focus and undo stack have settled.
        // Coalesce requests: UpdateCommandUI is expensive and the shell caches
        // statuses. Never call it recursively from QueryStatus.
        internal static void RefreshNativeCommands()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (commandUiRefreshQueued) return;
            commandUiRefreshQueued = true;
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
            {
                commandUiRefreshQueued = false;
                try
                {
                    var ui = Package.GetGlobalService(typeof(SVsUIShell)) as IVsUIShell;
                    if (ui != null) ui.UpdateCommandUI(0);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine("[Java U3C.3] UpdateCommandUI: " + ex.Message);
                }
            }), DispatcherPriority.Background);
        }

        internal static void Reset()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (var history in ObservedHistories)
            {
                history.UndoRedoHappened -= OnNativeUndoRedo;
                history.UndoTransactionCompleted -= OnNativeTransactionCompleted;
            }
            ObservedHistories.Clear();
            Entries.Clear();
            executing = false;
            lastAvailabilityDiagnostic = null;
            RefreshNativeCommands();
        }
    }

    // Priority targets are queried for EVERY command, so this handler is kept
    // extremely narrow. It NEVER claims Undo/Redo when an active editor has a
    // native action, and only covers Java documents or Solution Explorer.
    internal sealed class JavaPackageGlobalUndoTarget : IOleCommandTarget, IDisposable
    {
        private readonly System.IServiceProvider services;
        private IVsRegisterPriorityCommandTarget registration;
        private uint cookie;
        private bool disposed;
        // U3C9 CommandIdentity: record a bounded set of foreign command IDs.
        // A count=1 request must not be described as a "mixed batch".
        private readonly HashSet<string> observedCommandProbes =
            new HashSet<string>(StringComparer.Ordinal);
        private void ObserveCommand(Guid group, uint[] ids, string stage)
        {
            if (!JavaPackageGlobalUndo.HasTrackedRenames ||
                observedCommandProbes.Count >= 48) return;
            string key = stage + ":" + group.ToString("D") + ":" +
                string.Join(",", ids.Select(x => x.ToString()));
            if (!observedCommandProbes.Add(key)) return;
            JavaLanguageStatus.LogLifecycle("U3C9 " + stage +
                " guid=" + group.ToString("D") +
                "; ids=" + string.Join(",", ids.Select(x => x.ToString())) +
                "; VS97=" + (group == VSConstants.GUID_VSStandardCommandSet97) +
                "; VS2K=" + (group == VSConstants.VSStd2K));
        }
        // Log only on state transitions for each command/context. This also
        // reveals whether VS is asking our priority target for Undo/Redo.
        private readonly Dictionary<string, string> routeStates =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private void LogRoute(string key, string state)
        {
            string previous;
            if (routeStates.TryGetValue(key, out previous) &&
                string.Equals(previous, state, StringComparison.Ordinal)) return;
            routeStates[key] = state;
            JavaLanguageStatus.LogLifecycle("U3C8 routing " + key + ": " + state);
        }

        // Retain the DTE event source: an old solution's undo transactions must
        // never be offered to a subsequently opened Java project.
        private EnvDTE.SolutionEvents solutionEvents;
        private EnvDTE.WindowEvents windowEvents;
        private EnvDTE.DocumentEvents documentEvents; // U3C11

        internal JavaPackageGlobalUndoTarget(System.IServiceProvider services)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            this.services = services;
            registration = services.GetService(typeof(SVsRegisterPriorityCommandTarget))
                as IVsRegisterPriorityCommandTarget;
            if (registration == null)
                throw new InvalidOperationException("Priority command routing di Visual Studio non disponibile.");
            ErrorHandler.ThrowOnFailure(registration.RegisterPriorityCommandTarget(0, this, out cookie));
            Trace.WriteLine("[Java U3.C] Priority Undo/Redo registrato, cookie=" + cookie);
            JavaLanguageStatus.LogLifecycle("U3C.2 native target registrato; cookie=" + cookie);
            try
            {
                var dte = services.GetService(typeof(SDTE)) as DTE;
                if (dte != null)
                {
                    solutionEvents = dte.Events.SolutionEvents;
                    solutionEvents.AfterClosing += OnSolutionClosed;
                    windowEvents = dte.Events.WindowEvents;
                    windowEvents.WindowActivated += OnWindowActivated;
                    documentEvents = dte.Events.DocumentEvents;
                    documentEvents.DocumentClosing += OnDocumentClosing;
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[Java U3.C] SolutionEvents non disponibili: " + ex);
            }
        }

        private void OnDocumentClosing(EnvDTE.Document document)
        {
            if (disposed || !ThreadHelper.CheckAccess() || document == null) return;
            try { JavaPackageGlobalUndo.DocumentClosing(document.FullName); }
            catch (Exception ex)
            {
                JavaLanguageStatus.LogLifecycle("U3C11 DocumentClosing: " + ex.Message);
            }
        }

        private void OnSolutionClosed()
        {
            if (ThreadHelper.CheckAccess()) JavaPackageGlobalUndo.Reset();
        }

        private void OnWindowActivated(EnvDTE.Window gotFocus, EnvDTE.Window lostFocus)
        {
            // Switching editor tabs or returning to Solution Explorer changes
            // which native undo history should take precedence. The callback can
            // fire before focus routing settles; defer the command UI requery.
            if (disposed || !ThreadHelper.CheckAccess()) return;
            JavaLanguageStatus.LogLifecycle("U3C.3 native focus refresh: " +
                (gotFocus == null ? "nessuna finestra" : gotFocus.Kind));
            routeStates.Clear();
            JavaPackageGlobalUndo.RefreshNativeCommands();
        }

        // U3C10: VS Standard toolbar probes VSStd97 MultiLevelUndo (44)
        // and MultiLevelRedo (30), not necessarily Edit.Undo (43)/Redo (29).
        // Recognize them for native toolbar availability. A click may perform
        // ONE CPS transaction only; a multi-step payload is never claimed.
        private static bool IsMultiLevelCommand(Guid group, uint id)
        {
            return group == VSConstants.GUID_VSStandardCommandSet97 &&
                (id == (uint)VSConstants.VSStd97CmdID.MultiLevelUndo ||
                 id == (uint)VSConstants.VSStd97CmdID.MultiLevelRedo);
        }

        private static bool IsUndoCommand(Guid group, uint id, out bool redo)
        {
            redo = false;
            if (group == VSConstants.GUID_VSStandardCommandSet97)
            {
                redo = id == (uint)VSConstants.VSStd97CmdID.Redo ||
                    id == (uint)VSConstants.VSStd97CmdID.MultiLevelRedo;
                return id == (uint)VSConstants.VSStd97CmdID.Undo ||
                    id == (uint)VSConstants.VSStd97CmdID.MultiLevelUndo || redo;
            }
            if (group == VSConstants.VSStd2K)
            {
                redo = id == (uint)VSConstants.VSStd2KCmdID.REDO;
                return id == (uint)VSConstants.VSStd2KCmdID.UNDO || redo;
            }
            return false;
        }

        // U3C12: the standard Undo split button can request OLECMDTEXT.
        // Fill only the buffer supplied by Visual Studio and only when it
        // explicitly requests command name/status. No custom toolbar commands.
        private static string TryWriteNativeCommandText(IntPtr commandText,
            string caption)
        {
            if (commandText == IntPtr.Zero) return "pCmdText=null";
            if (string.IsNullOrWhiteSpace(caption)) return "caption unavailable";
            try
            {
                // OLECMDTEXT layout: ULONG cmdtextf, cwActual, cwBuf; WCHAR rgwz[1].
                // OLECMDTEXTF_NAME=1, OLECMDTEXTF_STATUS=2.
                int textFlags = System.Runtime.InteropServices.Marshal.ReadInt32(
                    commandText, 0);
                int capacity = System.Runtime.InteropServices.Marshal.ReadInt32(
                    commandText, 8);
                if ((textFlags & 3) == 0) return "textFlag=" + textFlags;
                if (capacity < 1 || capacity > 4096)
                    return "badCapacity=" + capacity;
                int characters = Math.Min(caption.Length, capacity - 1);
                for (int i = 0; i < characters; ++i)
                    System.Runtime.InteropServices.Marshal.WriteInt16(commandText,
                        12 + i * 2, unchecked((short)caption[i]));
                System.Runtime.InteropServices.Marshal.WriteInt16(commandText,
                    12 + characters * 2, (short)0);
                System.Runtime.InteropServices.Marshal.WriteInt32(commandText,
                    4, characters + 1);
                return "written=" + characters + "; flags=" + textFlags +
                    "; capacity=" + capacity;
            }
            catch (Exception ex) { return "error=" + ex.GetType().Name; }
        }

        // U3C8 routing: distinguish the active DTE document from the most
        // recently used editor view. GetActiveView(1) fails when the toolbar
        // itself has focus while QueryStatus runs; GetActiveView(0) avoids that
        // race. Always check the editor FilePath against the active DTE document.
        private bool ShouldIntercept(bool redo, out string reason)
        {
            reason = "NoCandidate";
            if (disposed) { reason = "Disposed"; return false; }
            string marker = JavaPackageGlobalUndo.CandidateMarker(redo);
            if (string.IsNullOrEmpty(marker))
            {
                reason = "NoCandidate; " + JavaPackageGlobalUndo.ExplainCandidate(redo);
                return false;
            }

            var dte = services.GetService(typeof(SDTE)) as DTE;
            if (dte == null) { reason = "NoDTE"; return false; }
            Window active = dte.ActiveWindow;
            if (active == null) { reason = "NoActiveWindow"; return false; }

            if (string.Equals(active.Kind, "Tool", StringComparison.OrdinalIgnoreCase))
            {
                // ObjectKind may refer to the hosted tool rather than the VS
                // window ID. Check the actual Solution Explorer HWND as well.
                bool sameWindow = false;
                try
                {
                    var explorer = dte.Windows.Item(
                        EnvDTE.Constants.vsWindowKindSolutionExplorer);
                    sameWindow = explorer != null && active.HWnd != IntPtr.Zero &&
                        active.HWnd == explorer.HWnd;
                }
                catch (Exception) { /* The tool may be closed. */ }
                if (!sameWindow)
                {
                    Guid kind, explorerKind;
                    sameWindow = Guid.TryParse(active.ObjectKind, out kind) &&
                        Guid.TryParse(EnvDTE.Constants.vsWindowKindSolutionExplorer,
                            out explorerKind) && kind == explorerKind;
                }
                reason = sameWindow ? "SolutionExplorer" : "OtherTool";
                return sameWindow;
            }

            if (!string.Equals(active.Kind, "Document", StringComparison.OrdinalIgnoreCase))
            {
                reason = "OtherWindow";
                return false;
            }

            var textManager = services.GetService(typeof(SVsTextManager)) as IVsTextManager;
            var componentModel = services.GetService(typeof(SComponentModel)) as IComponentModel;
            if (textManager == null || componentModel == null)
            {
                reason = "NoTextServices";
                return false;
            }
            var adapters = componentModel.GetService<IVsEditorAdaptersFactoryService>();
            var registry = componentModel.GetService<ITextUndoHistoryRegistry>();
            var textDocuments = componentModel.GetService<ITextDocumentFactoryService>();
            if (adapters == null || registry == null || textDocuments == null)
            {
                reason = "NoEditorAdaptersOrHistory";
                return false;
            }

            IVsTextView native;
            // 0 = last active view even when the editor has lost keyboard focus.
            if (ErrorHandler.Failed(textManager.GetActiveView(0, null, out native)) ||
                native == null)
            {
                reason = "NoRecentTextView";
                return false;
            }
            var view = adapters.GetWpfTextView(native);
            if (view == null || view.IsClosed ||
                !view.TextBuffer.ContentType.IsOfType("java"))
            {
                reason = "RecentViewNotJava";
                return false;
            }
            ITextDocument textDocument;
            if (!textDocuments.TryGetTextDocument(view.TextBuffer, out textDocument) ||
                textDocument == null)
            {
                reason = "NoTextDocument";
                return false;
            }

            // A non-Java active tab must NEVER inherit the last Java view.
            // A renamed Java tab is checked using its live ITextDocument path.
            string activePath;
            try
            {
                var document = active.Document ?? dte.ActiveDocument;
                activePath = document == null ? null : document.FullName;
                if (string.IsNullOrWhiteSpace(activePath) ||
                    string.IsNullOrWhiteSpace(textDocument.FilePath))
                {
                    reason = "MissingDocumentPath";
                    return false;
                }
                if (!string.Equals(System.IO.Path.GetFullPath(activePath),
                    System.IO.Path.GetFullPath(textDocument.FilePath),
                    StringComparison.OrdinalIgnoreCase))
                {
                    reason = "StaleTextView; active=" +
                        System.IO.Path.GetFileName(activePath) + "; recent=" +
                        System.IO.Path.GetFileName(textDocument.FilePath);
                    return false;
                }
            }
            catch (Exception ex)
            {
                reason = "DocumentPath:" + ex.GetType().Name;
                return false;
            }

            var local = registry.GetHistory(view.TextBuffer);
            if (local == null)
            {
                reason = "NoLocalHistory; doc=" +
                    System.IO.Path.GetFileName(textDocument.FilePath);
                return false;
            }
            bool localCanUndo = redo ? local.CanRedo : local.CanUndo;
            if (!localCanUndo)
            {
                reason = "JavaNoLocalUndo; doc=" +
                    System.IO.Path.GetFileName(textDocument.FilePath);
                return true;
            }

            // The local operation may be the SAME linked CPS transaction. In
            // that one case it is safe for the global bridge to execute it.
            // Otherwise, ordinary text editing ALWAYS keeps precedence.
            if (JavaPackageGlobalUndo.IsNativeTopForBuffer(
                view.TextBuffer, marker, redo))
            {
                reason = "JavaTopIsSamePackage";
                return true;
            }
            // A freshly opened clean Java tab can report BOTH managed CanUndo
            // and CanRedo even though the native OLE manager has neither stack.
            // Never infer emptiness merely from IsDirty=false: a clean document
            // can still have real edit history. Require explicit E_FAIL from
            // BOTH OLE stacks, no open text transaction, and a different history
            // from the original package anchor. Otherwise delegate to VS.
            if (!textDocument.IsDirty &&
                !JavaPackageGlobalUndo.IsTrackedAnchorHistory(local))
            {
                bool localTransactionKnownClosed = false;
                try { localTransactionKnownClosed = local.CurrentTransaction == null; }
                catch (Exception) { /* NotSupported: cannot prove safety. */ }
                if (localTransactionKnownClosed &&
                    JavaPackageGlobalUndo.AreNativeStacksEmpty(view.TextBuffer))
                {
                    reason = "U3C14 JavaLocalOleEmptyOverride; doc=" +
                        System.IO.Path.GetFileName(textDocument.FilePath) +
                        "; localCanUndo=" + local.CanUndo +
                        "; localCanRedo=" + local.CanRedo;
                    return true;
                }
            }
            reason = "JavaLocalUndoPrecedence; doc=" +
                System.IO.Path.GetFileName(textDocument.FilePath) +
                "; dirty=" + textDocument.IsDirty +
                "; sameAnchorHistory=" +
                JavaPackageGlobalUndo.IsTrackedAnchorHistory(local) +
                "; localCanUndo=" + local.CanUndo +
                "; localCanRedo=" + local.CanRedo + "; localTop=" +
                JavaPackageGlobalUndo.DescribeBufferTop(view.TextBuffer, redo);
            return false;
        }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds,
            IntPtr pCmdText)
        {
            bool knownGroup = pguidCmdGroup == VSConstants.GUID_VSStandardCommandSet97 ||
                pguidCmdGroup == VSConstants.VSStd2K;
            if (prgCmds == null || cCmds == 0 || cCmds > (uint)prgCmds.Length)
                return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
            if (!knownGroup)
            {
                // Only sample foreign groups that use an Undo-like numeric ID.
                // Never intercept them without knowing their semantics.
                if (cCmds == 1 && (prgCmds[0].cmdID == 43 ||
                    prgCmds[0].cmdID == 29 || prgCmds[0].cmdID == 71 ||
                    prgCmds[0].cmdID == 73))
                    ObserveCommand(pguidCmdGroup,
                        new uint[] { prgCmds[0].cmdID }, "QueryStatus/OtherGroup");
                return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_UNKNOWNGROUP;
            }
            // Avoid claiming a batch that contains unrelated commands.
            for (int i = 0; i < (int)cCmds; ++i)
            {
                bool ignored;
                if (!IsUndoCommand(pguidCmdGroup, prgCmds[i].cmdID, out ignored))
                {
                    if (JavaPackageGlobalUndo.HasTrackedRenames)
                    {
                        uint[] ids = new uint[cCmds];
                        for (int j = 0; j < (int)cCmds; ++j)
                            ids[j] = prgCmds[j].cmdID;
                        ObserveCommand(pguidCmdGroup, ids,
                            cCmds == 1 ? "QueryStatus/UnrecognizedSingle" :
                                "QueryStatus/UnrecognizedBatch");
                    }
                    return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
                }
            }
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                bool anySupported = false;
                for (int i = 0; i < (int)cCmds; ++i)
                {
                    bool redo;
                    IsUndoCommand(pguidCmdGroup, prgCmds[i].cmdID, out redo);
                    string reason;
                    bool handle = ShouldIntercept(redo, out reason);
                    LogRoute("QueryStatus/" +
                        (pguidCmdGroup == VSConstants.VSStd2K ? "VS2K/" : "VS97/") +
                        (IsMultiLevelCommand(pguidCmdGroup, prgCmds[i].cmdID) ?
                            "MultiLevel" : "") +
                        (redo ? "Redo" : "Undo"),
                        (handle ? "Enabled" : "Delegated") + "; " + reason);
                    if (handle)
                    {
                        prgCmds[i].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED |
                            OLECMDF.OLECMDF_ENABLED);
                        if (cCmds == 1 &&
                            IsMultiLevelCommand(pguidCmdGroup, prgCmds[i].cmdID))
                        {
                            string caption = JavaPackageGlobalUndo.CandidateCaption(redo);
                            string result = TryWriteNativeCommandText(pCmdText, caption);
                            LogRoute("U3C12 QueryStatus/CmdText/" +
                                (redo ? "Redo" : "Undo"), result);
                        }
                        anySupported = true;
                    }
                    else prgCmds[i].cmdf = 0;
                }
                return anySupported ? VSConstants.S_OK :
                    (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
            }
            catch (Exception ex)
            {
                LogRoute("QueryStatus/Exception", ex.GetType().Name);
                return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
            }
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt,
            IntPtr pvaIn, IntPtr pvaOut)
        {
            if (pguidCmdGroup != VSConstants.GUID_VSStandardCommandSet97 &&
                pguidCmdGroup != VSConstants.VSStd2K)
                return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_UNKNOWNGROUP;
            bool redo;
            if (!IsUndoCommand(pguidCmdGroup, nCmdID, out redo))
            {
                ObserveCommand(pguidCmdGroup, new uint[] { nCmdID },
                    "Exec/Unrecognized");
                return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
            }
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                // MultiLevel commands can carry a selection/count. We only
                // support the default single-click action; never silently
                // replace a requested multi-step Undo with one operation.
                if (IsMultiLevelCommand(pguidCmdGroup, nCmdID) &&
                    (nCmdexecopt != 0 || pvaIn != IntPtr.Zero))
                {
                    LogRoute("Exec/VS97/MultiLevel", "DelegatedNonDefault; opt=" +
                        nCmdexecopt + "; hasInput=" + (pvaIn != IntPtr.Zero));
                    return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
                }
                string reason;
                bool handle = ShouldIntercept(redo, out reason);
                LogRoute("Exec/" +
                    (pguidCmdGroup == VSConstants.VSStd2K ? "VS2K/" : "VS97/") +
                    (IsMultiLevelCommand(pguidCmdGroup, nCmdID) ? "MultiLevel" : "") +
                    (redo ? "Redo" : "Undo"),
                    (handle ? "Handled" : "Delegated") + "; " + reason);
                if (!handle)
                    return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
                // Once claimed, do NOT fall through to an unrelated local Undo
                // if the CPS operation changes during execution.
                if (!JavaPackageGlobalUndo.Execute(redo))
                    LogRoute("Exec/Skipped", "Il candidato CPS non e' piu' corrente");
                return VSConstants.S_OK;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[Java U3.C] Undo/Redo globale: " + ex);
                System.Windows.MessageBox.Show("Impossibile " +
                    (redo ? "ripetere" : "annullare") + " la rinomina del package.\n" +
                    ex.Message + "\nControlla i file e il backup prima di procedere.",
                    "Java: Undo/Redo package", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return VSConstants.S_OK;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (documentEvents != null)
            {
                try { documentEvents.DocumentClosing -= OnDocumentClosing; }
                catch (Exception ex) { Trace.WriteLine("[Java U3C11] DocumentEvents: " + ex); }
                documentEvents = null;
            }
            if (windowEvents != null)
            {
                try { windowEvents.WindowActivated -= OnWindowActivated; }
                catch (Exception ex) { Trace.WriteLine("[Java U3C.3] WindowEvents unsubscribe: " + ex); }
                windowEvents = null;
            }
            if (solutionEvents != null)
            {
                try { solutionEvents.AfterClosing -= OnSolutionClosed; }
                catch (Exception ex) { Trace.WriteLine("[Java U3.C] SolutionEvents unsubscribe: " + ex); }
                solutionEvents = null;
            }
            if (registration != null && cookie != 0)
            {
                try { registration.UnregisterPriorityCommandTarget(cookie); }
                catch (Exception ex) { Trace.WriteLine("[Java U3.C] Unregister: " + ex); }
            }
            cookie = 0;
            registration = null;
            if (ThreadHelper.CheckAccess()) JavaPackageGlobalUndo.Reset();
        }
    }
}
