// VS 2026 shared native Undo/Redo bridge (U3.C.16).
// One priority target for all Java refactorings; the editor owns local text Undo.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Threading;
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

namespace JavaLanguageSupport.Editor.Undo
{
    internal static class JavaGlobalUndo
    {
        // The stored operation is independent from the feature which created it.
        // Its identity is the native COM marker, NOT an unsupported history stack.
        private sealed class Entry
        {
            internal ITextUndoHistory History;
            internal ITextUndoTransaction Transaction;
            internal ITextUndoPrimitive Primitive;
            internal IOleUndoManager NativeUndo;
            internal string Marker;
            internal Func<bool> IsApplied;
            internal string Caption;
            internal string UndoCaption;
            internal string RedoCaption;
            internal string AnchorPath;
            internal string OriginalAnchorPath;
            internal bool AnchorClosed;
            internal Func<bool, bool> CanDetached;
            internal Action<bool> ReplayDetached;
        }

        private const int MaxEntries = 32;
        private static readonly List<Entry> Entries = new List<Entry>();
        private static bool executing;
        // Histories can change through the editor,
        // not just through command routing. Observe them.
        private static readonly HashSet<ITextUndoHistory> ObservedHistories =
            new HashSet<ITextUndoHistory>();
        private static bool commandUiRefreshQueued;

        // Register a completed native-linked transaction from any Java feature.
        // The originator owns the primitive and its detached replay implementation.
        // A marker may be reused ONLY for the same transaction; generate a new
        // marker for each distinct operation (e.g. rename package, move type).
        internal static bool Register(JavaGlobalUndoOperation operation)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (operation == null || operation.History == null ||
                operation.Transaction == null || operation.Primitive == null ||
                operation.IsApplied == null || operation.AnchorBuffer == null ||
                string.IsNullOrWhiteSpace(operation.Marker) ||
                string.IsNullOrWhiteSpace(operation.Caption) ||
                string.IsNullOrWhiteSpace(operation.UndoCaption) ||
                string.IsNullOrWhiteSpace(operation.RedoCaption) ||
                string.IsNullOrWhiteSpace(operation.AnchorPath) ||
                string.IsNullOrWhiteSpace(operation.OriginalAnchorPath) ||
                (operation.CanReplayDetached == null) !=
                    (operation.ReplayDetached == null))
                return false;

            // VS2026 may throw NotSupportedException for LastUndoTransaction,
            // UndoStack and UndoDescription. Use the completed transaction and
            // validate the top native OLE description by its unique marker.
            var transaction = operation.Transaction;
            bool containsPrimitive = ReferenceEquals(operation.Primitive.Parent,
                transaction);
            if (!containsPrimitive)
            {
                try
                {
                    containsPrimitive = transaction.UndoPrimitives != null &&
                        transaction.UndoPrimitives.Any(x =>
                            ReferenceEquals(x, operation.Primitive));
                }
                catch (NotSupportedException) { }
            }

            string status;
            var nativeUndo = GetNativeUndoManager(operation.AnchorBuffer, out status);
            if (!operation.History.CanUndo || !operation.IsApplied() ||
                !containsPrimitive ||
                !IsTopTransaction(nativeUndo, operation.Marker, false))
            {
                Trace.WriteLine("[Java Undo] Registrazione rifiutata: " +
                    operation.Caption + "; nativeManager=" + status);
                return false;
            }

            // Preserve undo-stack ordering:
            // an applied operation discards the abandoned global redo branch.
            Entries.RemoveAll(e => !SafeApplied(e));
            Entries.Add(new Entry {
                History = operation.History,
                Transaction = transaction,
                Primitive = operation.Primitive,
                NativeUndo = nativeUndo,
                Marker = operation.Marker,
                IsApplied = operation.IsApplied,
                Caption = operation.Caption,
                UndoCaption = operation.UndoCaption,
                RedoCaption = operation.RedoCaption,
                AnchorPath = System.IO.Path.GetFullPath(operation.AnchorPath),
                OriginalAnchorPath = System.IO.Path.GetFullPath(
                    operation.OriginalAnchorPath),
                CanDetached = operation.CanReplayDetached,
                ReplayDetached = operation.ReplayDetached
            });
            if (Entries.Count > MaxEntries) Entries.RemoveAt(0);
            WatchHistory(operation.History);
            RefreshNativeCommands();
            return true;
        }

        // Closing the anchor invalidates its buffer history. A newly opened
        // editor is a different history; detached replay is feature-owned.
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

        // VS2026 can expose an ITextUndoHistory implementation whose
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

        // Native OLE stack identity: do not consume unrelated editor edits.
        private static bool IsTopTransaction(IOleUndoManager nativeUndo,
            string marker, bool redo)
        {
            if (nativeUndo == null || string.IsNullOrWhiteSpace(marker))
                return false;
            try
            {
                string description;
                if (redo) nativeUndo.GetLastRedoDescription(out description);
                else nativeUndo.GetLastUndoDescription(out description);
                return !string.IsNullOrEmpty(description) &&
                    description.IndexOf("[" + marker + "]",
                        StringComparison.Ordinal) >= 0;
            }
            catch (Exception) { return false; }
        }

        // When another editor is active, the source-buffer COM
        // manager can tell whether its own top operation is the SAME tracked operation
        // rename. Never take over unrelated local text Undo/Redo.
        internal static bool IsNativeTopForBuffer(ITextBuffer buffer, string marker,
            bool redo)
        {
            string status;
            var manager = GetNativeUndoManager(buffer, out status);
            return IsTopTransaction(manager, marker, redo);
        }

        // The same toolbar/list protocol is used by every registered feature.
        internal static string CandidateCaption(bool redo)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Entry entry = Candidate(redo);
            return entry == null ? null :
                (redo ? entry.RedoCaption : entry.UndoCaption);
        }

        // Local COM HRESULT: distinguish empty OLE undo (E_FAIL) from
        // disabled/failed managers. CanUndo true alone is NOT a reason to
        // override this history, even when the document is saved.
        internal static bool IsTrackedAnchorHistory(ITextUndoHistory history)
        {
            return history != null && Entries.Any(x => !x.AnchorClosed &&
                ReferenceEquals(x.History, history));
        }

        // This override is only valid for a clean, unrelated Java tab.
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
            // Never query a closed (potentially disposed) history.
            if (entry.AnchorClosed)
            {
                try { return entry.CanDetached != null && entry.CanDetached(redo)
                    ? entry : null; }
                catch (Exception) { return null; }
            }
            try
            {
                // Do not undo unrelated text edits that appeared on top of
                // the registered transaction in its original anchor buffer.
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

        // Expose the validated operation marker to the priority command target.
        // Candidate checks the operation anchor's real COM undo top first.
        internal static string CandidateMarker(bool redo)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Entry entry = Candidate(redo);
            return entry == null ? null : entry.Marker;
        }

        internal static bool CanExecute(bool redo)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return Candidate(redo) != null;
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
                    target.ReplayDetached(redo);
                }
                else if (redo) target.History.Redo(1);
                else target.History.Undo(1);
                Trace.WriteLine("[Java Undo] " + (redo ? "Ripeti " : "Annulla ") + target.Caption);
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
                    Trace.WriteLine("[Java Undo] UpdateCommandUI: " + ex.Message);
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
            RefreshNativeCommands();
        }
    }

}
