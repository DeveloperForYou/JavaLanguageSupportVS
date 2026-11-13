// U3.C - Java CPS package rename: optional IDE-level Undo/Redo bridge.
// Only eligible package operations are handled. Other commands stay native.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
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
            internal Func<bool> IsApplied;
            internal string Caption;
        }

        private const int MaxEntries = 32;
        private static readonly List<Entry> Entries = new List<Entry>();
        private static bool executing;

        // Called AFTER the native VS linked transaction has been completed.
        // Never fabricate a second undo history: we must use the actual history
        // that owns PackageRenameUndoPrimitive and its text edits.
        internal static void Remember(ITextUndoHistory history, ITextUndoPrimitive primitive,
            Func<bool> isApplied, string oldPackage, string newPackage)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (history == null || primitive == null || isApplied == null) return;
            ITextUndoTransaction top = history.LastUndoTransaction;
            bool containsPrimitive = top != null && top.UndoPrimitives != null &&
                top.UndoPrimitives.Any(item => ReferenceEquals(item, primitive));
            if (top == null || !history.CanUndo || !isApplied() || !containsPrimitive)
            {
                // U3C.2: keep the safety check. Diagnose WHY the native command
                // is unavailable; do not relax identity checks and undo other edits.
                JavaLanguageStatus.LogLifecycle("U3C.2 Undo registrazione rifiutata: " +
                    "top=" + (top != null) + "; canUndo=" + history.CanUndo +
                    "; primitivePresente=" + containsPrimitive +
                    "; primitiveCount=" + (top == null || top.UndoPrimitives == null
                        ? 0 : top.UndoPrimitives.Count));
                return;
            }
            // A new refactoring invalidates the global redo branch, just as a
            // new native edit invalidates redo in a conventional undo stack.
            Entries.RemoveAll(e => !SafeApplied(e));
            Entries.Add(new Entry {
                History = history, Transaction = top, IsApplied = isApplied,
                Caption = oldPackage + " -> " + newPackage
            });
            if (Entries.Count > MaxEntries) Entries.RemoveAt(0);
            JavaLanguageStatus.LogLifecycle("U3C.2 Undo registrato: " +
                oldPackage + " -> " + newPackage);
            RefreshNativeCommands();
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
            try
            {
                // Do not undo unrelated text edits that appeared on top of
                // the package transaction in its original anchor buffer.
                if (entry.History.CurrentTransaction != null) return null;
                if (redo)
                    return entry.History.CanRedo &&
                        ReferenceEquals(entry.History.LastRedoTransaction, entry.Transaction)
                        ? entry : null;
                return entry.History.CanUndo &&
                    ReferenceEquals(entry.History.LastUndoTransaction, entry.Transaction)
                    ? entry : null;
            }
            catch (Exception) { return null; }
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
                    topMatch = ReferenceEquals(redo ? entry.History.LastRedoTransaction :
                        entry.History.LastUndoTransaction, entry.Transaction);
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
                if (redo) target.History.Redo(1);
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

        // Ask VS to requery the *native* Edit.Undo/Edit.Redo toolbar commands.
        // No separate commands or shortcuts are introduced by this extension.
        private static void RefreshNativeCommands()
        {
            try
            {
                var ui = Package.GetGlobalService(typeof(SVsUIShell)) as IVsUIShell;
                if (ui != null) ui.UpdateCommandUI(0);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[Java U3C.2] UpdateCommandUI: " + ex.Message);
            }
        }

        internal static void Reset()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Entries.Clear();
            executing = false;
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
        // Retain the DTE event source: an old solution's undo transactions must
        // never be offered to a subsequently opened Java project.
        private EnvDTE.SolutionEvents solutionEvents;

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
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[Java U3.C] SolutionEvents non disponibili: " + ex);
            }
        }

        private void OnSolutionClosed()
        {
            if (ThreadHelper.CheckAccess()) JavaPackageGlobalUndo.Reset();
        }

        private static bool IsUndoCommand(uint id, out bool redo)
        {
            redo = id == (uint)VSConstants.VSStd97CmdID.Redo;
            return id == (uint)VSConstants.VSStd97CmdID.Undo || redo;
        }

        private bool ShouldIntercept(bool redo)
        {
            if (disposed || !JavaPackageGlobalUndo.CanExecute(redo)) return false;
            var dte = services.GetService(typeof(SDTE)) as DTE;
            if (dte == null) return false;
            Window active = dte.ActiveWindow;
            if (active == null) return false;
            if (string.Equals(active.Kind, "Tool", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(active.ObjectKind,
                    EnvDTE.Constants.vsWindowKindSolutionExplorer,
                    StringComparison.OrdinalIgnoreCase);
            }
            if (!string.Equals(active.Kind, "Document", StringComparison.OrdinalIgnoreCase))
                return false;

            var textManager = services.GetService(typeof(SVsTextManager)) as IVsTextManager;
            var componentModel = services.GetService(typeof(SComponentModel)) as IComponentModel;
            if (textManager == null || componentModel == null) return false;
            var adapters = componentModel.GetService<IVsEditorAdaptersFactoryService>();
            var histories = componentModel.GetService<ITextUndoHistoryRegistry>();
            if (adapters == null || histories == null) return false;
            IVsTextView native;
            if (textManager.GetActiveView(1, null, out native) < 0 || native == null)
                return false;
            var view = adapters.GetWpfTextView(native);
            if (view == null || view.IsClosed || !view.TextBuffer.ContentType.IsOfType("java"))
                return false;
            var local = histories.GetHistory(view.TextBuffer);
            if (local == null) return false;
            // The active editor owns its native text undo before the CPS bridge.
            return redo ? !local.CanRedo : !local.CanUndo;
        }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds,
            IntPtr pCmdText)
        {
            if (pguidCmdGroup != VSConstants.GUID_VSStandardCommandSet97)
                return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_UNKNOWNGROUP;
            if (prgCmds == null || cCmds != 1 || prgCmds.Length < 1)
                return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
            bool redo;
            if (!IsUndoCommand(prgCmds[0].cmdID, out redo))
                return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (!ShouldIntercept(redo))
                    return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
                prgCmds[0].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_ENABLED);
                return VSConstants.S_OK;
            }
            catch (Exception) // QueryStatus must never interrupt VS routing.
            {
                return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
            }
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt,
            IntPtr pvaIn, IntPtr pvaOut)
        {
            if (pguidCmdGroup != VSConstants.GUID_VSStandardCommandSet97)
                return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_UNKNOWNGROUP;
            bool redo;
            if (!IsUndoCommand(nCmdID, out redo))
                return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (!ShouldIntercept(redo))
                    return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
                // Command belongs to the package; do not fall through to a
                // different native Undo if the CPS primitive detects a conflict.
                JavaPackageGlobalUndo.Execute(redo);
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
