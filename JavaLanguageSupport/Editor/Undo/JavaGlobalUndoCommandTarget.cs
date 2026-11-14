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
    internal class JavaGlobalUndoCommandTarget : IOleCommandTarget, IDisposable
    {
        private readonly System.IServiceProvider services;
        private IVsRegisterPriorityCommandTarget registration;
        private uint cookie;
        private bool disposed;
        // Retain the DTE event source: an old solution's undo transactions must
        // never be offered to a subsequently opened Java project.
        private EnvDTE.SolutionEvents solutionEvents;
        private EnvDTE.WindowEvents windowEvents;
        private EnvDTE.DocumentEvents documentEvents;

        internal JavaGlobalUndoCommandTarget(System.IServiceProvider services)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            this.services = services;
            registration = services.GetService(typeof(SVsRegisterPriorityCommandTarget))
                as IVsRegisterPriorityCommandTarget;
            if (registration == null)
                throw new InvalidOperationException("Priority command routing di Visual Studio non disponibile.");
            ErrorHandler.ThrowOnFailure(registration.RegisterPriorityCommandTarget(0, this, out cookie));
            Trace.WriteLine("[Java Undo] Native bridge active; cookie=" + cookie);
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
                Trace.WriteLine("[Java Undo] VS events unavailable: " + ex);
            }
        }

        private void OnDocumentClosing(EnvDTE.Document document)
        {
            if (disposed || !ThreadHelper.CheckAccess() || document == null) return;
            try { JavaGlobalUndo.DocumentClosing(document.FullName); }
            catch (Exception ex)
            {
                Trace.WriteLine("[Java Undo] DocumentClosing: " + ex);
            }
        }

        private void OnSolutionClosed()
        {
            if (ThreadHelper.CheckAccess()) JavaGlobalUndo.Reset();
        }

        private void OnWindowActivated(EnvDTE.Window gotFocus, EnvDTE.Window lostFocus)
        {
            // Switching editor tabs or returning to Solution Explorer changes
            // which native undo history should take precedence. The callback can
            // fire before focus routing settles; defer the command UI requery.
            if (disposed || !ThreadHelper.CheckAccess()) return;
            JavaGlobalUndo.RefreshNativeCommands();
        }

        // VS Standard toolbar probes VSStd97 MultiLevelUndo (44)
        // and MultiLevelRedo (30), not necessarily Edit.Undo (43)/Redo (29).
        // Recognize them for native toolbar availability. A click may perform
        // ONE CPS transaction only; a multi-step payload is never claimed.
        private static bool IsMultiLevelCommand(Guid group, uint id)
        {
            return group == VSConstants.GUID_VSStandardCommandSet97 &&
                (id == (uint)VSConstants.VSStd97CmdID.MultiLevelUndo ||
                 id == (uint)VSConstants.VSStd97CmdID.MultiLevelRedo);
        }

        // Native multi-level caption list. IDs from the VS97 standard command
        // set: cmdidMultiLevelUndoList=298; cmdidMultiLevelRedoList=299.
        // The toolbar's combo/list requests these SEPARATELY from the actual
        // MultiLevelUndo (44) and MultiLevelRedo (30) commands.
        private static bool IsMultiLevelListCommand(Guid group, uint id, out bool redo)
        {
            redo = group == VSConstants.GUID_VSStandardCommandSet97 && id == 299U;
            return group == VSConstants.GUID_VSStandardCommandSet97 &&
                (id == 298U || redo);
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

        // Some shell implementations request text via OLECMDTEXT; others use
        // VSStd97 MultiLevelUndoList/RedoList. We support BOTH existing routes.
        private static void WriteNativeCommandText(IntPtr commandText,
            string caption)
        {
            if (commandText == IntPtr.Zero || string.IsNullOrWhiteSpace(caption))
                return;
            try
            {
                int flags = System.Runtime.InteropServices.Marshal.ReadInt32(
                    commandText, 0);
                int capacity = System.Runtime.InteropServices.Marshal.ReadInt32(
                    commandText, 8);
                if ((flags & 3) == 0 || capacity < 1 || capacity > 4096) return;
                int characters = Math.Min(caption.Length, capacity - 1);
                for (int i = 0; i < characters; ++i)
                    System.Runtime.InteropServices.Marshal.WriteInt16(commandText,
                        12 + i * 2, unchecked((short)caption[i]));
                System.Runtime.InteropServices.Marshal.WriteInt16(commandText,
                    12 + characters * 2, (short)0);
                System.Runtime.InteropServices.Marshal.WriteInt32(commandText,
                    4, characters + 1);
            }
            catch (Exception) { /* The shell supplied no usable text buffer. */ }
        }

        // Claim the command only for a verified CPS candidate. The active
        // Java document keeps priority whenever it has a real local OLE action.
        // A clean unrelated tab with BOTH native stacks empty may use the
        // global operation even if its MEF history reports CanUndo=true.
        private bool ShouldIntercept(bool redo)
        {
            if (disposed || string.IsNullOrEmpty(JavaGlobalUndo.CandidateMarker(redo)))
                return false;

            var dte = services.GetService(typeof(SDTE)) as DTE;
            if (dte == null) return false;
            Window active = dte.ActiveWindow;
            if (active == null) return false;

            if (string.Equals(active.Kind, "Tool", StringComparison.OrdinalIgnoreCase))
            {
                bool sameWindow = false;
                try
                {
                    var explorer = dte.Windows.Item(
                        EnvDTE.Constants.vsWindowKindSolutionExplorer);
                    sameWindow = explorer != null && active.HWnd != IntPtr.Zero &&
                        active.HWnd == explorer.HWnd;
                }
                catch (Exception) { }
                if (!sameWindow)
                {
                    Guid kind, explorerKind;
                    sameWindow = Guid.TryParse(active.ObjectKind, out kind) &&
                        Guid.TryParse(EnvDTE.Constants.vsWindowKindSolutionExplorer,
                            out explorerKind) && kind == explorerKind;
                }
                return sameWindow;
            }

            if (!string.Equals(active.Kind, "Document", StringComparison.OrdinalIgnoreCase))
                return false;

            var textManager = services.GetService(typeof(SVsTextManager)) as IVsTextManager;
            var model = services.GetService(typeof(SComponentModel)) as IComponentModel;
            if (textManager == null || model == null) return false;
            var adapters = model.GetService<IVsEditorAdaptersFactoryService>();
            var registry = model.GetService<ITextUndoHistoryRegistry>();
            var textDocuments = model.GetService<ITextDocumentFactoryService>();
            if (adapters == null || registry == null || textDocuments == null)
                return false;

            IVsTextView native;
            if (ErrorHandler.Failed(textManager.GetActiveView(0, null, out native)) ||
                native == null) return false;
            var view = adapters.GetWpfTextView(native);
            if (view == null || view.IsClosed ||
                !view.TextBuffer.ContentType.IsOfType("java")) return false;
            ITextDocument textDocument;
            if (!textDocuments.TryGetTextDocument(view.TextBuffer, out textDocument) ||
                textDocument == null) return false;
            try
            {
                var document = active.Document ?? dte.ActiveDocument;
                if (document == null || string.IsNullOrWhiteSpace(document.FullName) ||
                    string.IsNullOrWhiteSpace(textDocument.FilePath) ||
                    !string.Equals(System.IO.Path.GetFullPath(document.FullName),
                        System.IO.Path.GetFullPath(textDocument.FilePath),
                        StringComparison.OrdinalIgnoreCase)) return false;
            }
            catch (Exception) { return false; }

            var local = registry.GetHistory(view.TextBuffer);
            if (local == null) return false;
            if (!(redo ? local.CanRedo : local.CanUndo)) return true;
            var marker = JavaGlobalUndo.CandidateMarker(redo);
            if (JavaGlobalUndo.IsNativeTopForBuffer(view.TextBuffer, marker, redo))
                return true;

            if (!textDocument.IsDirty && !JavaGlobalUndo.IsTrackedAnchorHistory(local))
            {
                // Do not guess if CurrentTransaction isn't supported.
                bool closed = false;
                try { closed = local.CurrentTransaction == null; }
                catch (Exception) { }
                if (closed && JavaGlobalUndo.AreNativeStacksEmpty(view.TextBuffer))
                    return true;
            }
            return false;
        }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds,
            IntPtr pCmdText)
        {
            if (prgCmds == null || cCmds == 0 || cCmds > (uint)prgCmds.Length)
                return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;

            bool listRedo;
            if (cCmds == 1 && IsMultiLevelListCommand(
                pguidCmdGroup, prgCmds[0].cmdID, out listRedo))
            {
                try
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    if (!ShouldIntercept(listRedo))
                        return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
                    prgCmds[0].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED |
                        OLECMDF.OLECMDF_ENABLED);
                    return VSConstants.S_OK;
                }
                catch (Exception)
                {
                    return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
                }
            }

            if (pguidCmdGroup != VSConstants.GUID_VSStandardCommandSet97 &&
                pguidCmdGroup != VSConstants.VSStd2K)
                return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_UNKNOWNGROUP;

            // Don't claim mixed batches or unrelated commands.
            for (int i = 0; i < (int)cCmds; ++i)
            {
                bool redo;
                if (!IsUndoCommand(pguidCmdGroup, prgCmds[i].cmdID, out redo))
                    return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
            }

            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                bool anySupported = false;
                for (int i = 0; i < (int)cCmds; ++i)
                {
                    bool redo;
                    IsUndoCommand(pguidCmdGroup, prgCmds[i].cmdID, out redo);
                    if (ShouldIntercept(redo))
                    {
                        prgCmds[i].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED |
                            OLECMDF.OLECMDF_ENABLED);
                        if (cCmds == 1 &&
                            IsMultiLevelCommand(pguidCmdGroup, prgCmds[i].cmdID))
                            WriteNativeCommandText(pCmdText,
                                JavaGlobalUndo.CandidateCaption(redo));
                        anySupported = true;
                    }
                    else prgCmds[i].cmdf = 0;
                }
                return anySupported ? VSConstants.S_OK :
                    (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
            }
            catch (Exception)
            {
                return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
            }
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt,
            IntPtr pvaIn, IntPtr pvaOut)
        {
            if (pguidCmdGroup != VSConstants.GUID_VSStandardCommandSet97 &&
                pguidCmdGroup != VSConstants.VSStd2K)
                return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_UNKNOWNGROUP;

            // VS97 multi-level GetList: exactly one verified operation label.
            bool listRedo;
            if (IsMultiLevelListCommand(pguidCmdGroup, nCmdID, out listRedo))
            {
                try
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    if (nCmdexecopt != 0 || pvaOut == IntPtr.Zero ||
                        pvaIn != IntPtr.Zero || !ShouldIntercept(listRedo))
                        return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
                    string caption = JavaGlobalUndo.CandidateCaption(listRedo);
                    if (string.IsNullOrEmpty(caption))
                        return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
                    System.Runtime.InteropServices.Marshal.GetNativeVariantForObject(
                        new string[] { caption }, pvaOut);
                    return VSConstants.S_OK;
                }
                catch (Exception)
                {
                    return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
                }
            }

            bool redo;
            if (!IsUndoCommand(pguidCmdGroup, nCmdID, out redo))
                return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (IsMultiLevelCommand(pguidCmdGroup, nCmdID) &&
                    pvaOut != IntPtr.Zero)
                {
                    if (nCmdexecopt != 0 || pvaIn != IntPtr.Zero ||
                        !ShouldIntercept(redo))
                        return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
                    string caption = JavaGlobalUndo.CandidateCaption(redo);
                    if (string.IsNullOrEmpty(caption))
                        return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
                    System.Runtime.InteropServices.Marshal.GetNativeVariantForObject(
                        caption, pvaOut);
                    return VSConstants.S_OK;
                }

                // Never silently interpret a multi-step selection as a single step.
                if (IsMultiLevelCommand(pguidCmdGroup, nCmdID) &&
                    (nCmdexecopt != 0 || pvaIn != IntPtr.Zero))
                {
                    if (nCmdexecopt != 0 || pvaIn == IntPtr.Zero)
                        return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
                    object selected;
                    try
                    {
                        selected = System.Runtime.InteropServices.Marshal
                            .GetObjectForNativeVariant(pvaIn);
                    }
                    catch (Exception)
                    {
                        return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
                    }
                    string caption = JavaGlobalUndo.CandidateCaption(redo);
                    bool singleSelection =
                        (selected is string && string.Equals((string)selected,
                            caption, StringComparison.Ordinal)) ||
                        (selected is int && ((int)selected == 0 || (int)selected == 1));
                    if (!singleSelection)
                        return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
                }
                if (!ShouldIntercept(redo))
                    return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;

                // Once claimed, do not fall through to unrelated local Undo.
                JavaGlobalUndo.Execute(redo);
                return VSConstants.S_OK;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[Java Undo] Esecuzione globale: " + ex);
                System.Windows.MessageBox.Show("Impossibile " +
                    (redo ? "ripetere" : "annullare") +
                    " l'operazione Java.\n" + ex.Message +
                    "\nControlla i file e il backup prima di procedere.",
                    "Java: Undo/Redo", System.Windows.MessageBoxButton.OK,
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
                catch (Exception ex) { Trace.WriteLine("[Java Undo] DocumentEvents: " + ex); }
                documentEvents = null;
            }
            if (windowEvents != null)
            {
                try { windowEvents.WindowActivated -= OnWindowActivated; }
                catch (Exception ex) { Trace.WriteLine("[Java Undo] WindowEvents unsubscribe: " + ex); }
                windowEvents = null;
            }
            if (solutionEvents != null)
            {
                try { solutionEvents.AfterClosing -= OnSolutionClosed; }
                catch (Exception ex) { Trace.WriteLine("[Java Undo] SolutionEvents unsubscribe: " + ex); }
                solutionEvents = null;
            }
            if (registration != null && cookie != 0)
            {
                try { registration.UnregisterPriorityCommandTarget(cookie); }
                catch (Exception ex) { Trace.WriteLine("[Java Undo] Unregister: " + ex); }
            }
            cookie = 0;
            registration = null;
            if (ThreadHelper.CheckAccess()) JavaGlobalUndo.Reset();
        }
    }
}
