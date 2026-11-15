// U4.C - Shared native Visual Studio Undo/Redo for Java WorkspaceEdit.
// Code actions, Quick Fix and Organize Imports use the existing JavaGlobalUndo bridge.
using System;
using System.Diagnostics;
using System.Linq;
using JavaLanguageSupport.Editor.Undo;
using Microsoft.VisualStudio.Text.Operations;

namespace JavaLanguageSupport.Editor.Refactoring
{
    internal static partial class JavaWorkspaceEditService
    {
        // Titles supplied by JDT LS are user-facing; do not allow multiline or huge toolbar labels.
        private static string SafeWorkspaceCaption(string title, int fileCount)
        {
            string value = string.IsNullOrWhiteSpace(title)
                ? "Modifiche Java (" + fileCount + " file)" : title.Trim();
            value = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            return value.Length <= 120 ? value : value.Substring(0, 117) + "...";
        }

        private static void RegisterSharedWorkspaceUndo(Plan plan, DocumentChange anchor,
            ITextUndoHistory history, ITextUndoTransaction transaction,
            WorkspaceSaveUndoPrimitive primitive, string marker,
            WorkspaceEditDetachedState detached, Action<string> log)
        {
            if (plan == null || anchor == null || history == null || transaction == null ||
                primitive == null || string.IsNullOrWhiteSpace(marker)) return;
            if (detached != null) detached.Bind(primitive);

            string title = plan.Caption;
            try
            {
                bool registered = JavaGlobalUndo.Register(new JavaGlobalUndoOperation
                {
                    History = history,
                    Transaction = transaction,
                    Primitive = primitive,
                    AnchorBuffer = anchor.Buffer,
                    Marker = marker,
                    IsApplied = () => primitive.IsApplied,
                    Caption = "Azione Java: " + title,
                    UndoCaption = "Annulla: " + title,
                    RedoCaption = "Ripeti: " + title,
                    AnchorPath = anchor.Path,
                    OriginalAnchorPath = anchor.Path,
                    RequiredOpenDocumentPaths = plan.Documents.Select(x => x.Path)
                        .Distinct(Paths).ToArray(),
                    CanReplayDetached = detached == null ? null :
                        new Func<bool, bool>(detached.CanReplayDetached),
                    ReplayDetached = detached == null ? null :
                        new Action<bool>(detached.ReplayDetached)
                });
                if (!registered)
                {
                    Trace.WriteLine("[Java U4.C] Registrazione Undo globale non disponibile: " + title);
                    if (log != null) log("WorkspaceEdit: Undo globale non registrato; " +
                        "rimane l'Undo locale dell'editor.");
                }
            }
            catch (Exception error)
            {
                // The linked transaction has already committed. Never roll it back here.
                Trace.WriteLine("[Java U4.C] Registrazione Undo globale non riuscita: " + error);
                if (log != null) log("WorkspaceEdit completato; " +
                    "registrazione Undo globale non riuscita.");
            }
        }
    }
}
