// U4A-SYMBOL-GLOBAL-UNDO: integrate Java symbol rename with the single
// U3.C.16 native Visual Studio Undo/Redo bridge (no extra command target).
// The underlying primitive is still responsible for the linked editor history.
using System;
using System.Linq;
using JavaLanguageSupport.Editor.Undo;
using Microsoft.VisualStudio.Text.Operations;

namespace JavaLanguageSupport.Editor.Refactoring
{
    internal static partial class JavaProjectRenameEdits
    {
        private static bool RegisterSharedSymbolUndo(RenamePlan plan, Target anchor,
            ITextUndoHistory history, ITextUndoTransaction transaction,
            RenameFileUndoPrimitive primitive, string marker)
        {
            if (plan == null || anchor == null || history == null ||
                transaction == null || primitive == null)
                return false;

            // The editor buffer retains its identity when a top-level Java type
            // rename changes the physical filename. Detect closing via BOTH paths.
            FileRename anchorMove = plan.moves.FirstOrDefault(x =>
                PathCompare.Equals(x.From, anchor.Path));
            string afterAnchorPath = anchorMove == null ? anchor.Path : anchorMove.To;
            string operation = plan.OldName + " -> " + plan.NewName;

            return JavaGlobalUndo.Register(new JavaGlobalUndoOperation
            {
                History = history,
                Transaction = transaction,
                Primitive = primitive,
                AnchorBuffer = anchor.Buffer,
                Marker = marker,
                IsApplied = () => primitive.IsApplied,
                Caption = "Rinomina simbolo Java: " + operation,
                UndoCaption = "Annulla rinomina simbolo Java: " + operation,
                RedoCaption = "Ripeti rinomina simbolo Java: " + operation,
                AnchorPath = afterAnchorPath,
                OriginalAnchorPath = anchor.Path,
                // All affected live buffers are part of the linked transaction.
                // Never let the shared priority target undo a closed target's
                // disposed history, even when the anchor itself remains open.
                RequiredOpenDocumentPaths = plan.targets.Select(x => x.Path)
                    .Concat(plan.moves.Select(x => x.To))
                    .Distinct(PathCompare).ToArray()
                // Deliberately omit detached callbacks. The original symbol
                // rename retains references to live Target buffers; replaying
                // those after their window closes is unsafe. The shared bridge
                // refuses this operation once its anchor has been closed.
            });
        }
    }
}
