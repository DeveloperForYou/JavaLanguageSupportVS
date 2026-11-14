// Package rename adapter for the shared Java native Undo/Redo bridge.
// Other refactorings call JavaGlobalUndo.Register(JavaGlobalUndoOperation).
using System;
using JavaLanguageSupport.Editor.Undo;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Operations;

namespace JavaLanguageSupport.ProjectSystem.Packages
{
    internal static class JavaPackageGlobalUndo
    {
        // Keep the original call signature to avoid changing tested CPS code.
        internal static void Remember(ITextUndoHistory history, ITextBuffer anchorBuffer,
            ITextUndoTransaction completedTransaction, ITextUndoPrimitive primitive,
            string marker, Func<bool> isApplied, string oldPackage, string newPackage,
            string anchorPath, string originalAnchorPath,
            Func<bool, bool> canDetached, Action<bool> replayDetached)
        {
            string label = oldPackage + " -> " + newPackage;
            if (!JavaGlobalUndo.Register(new JavaGlobalUndoOperation {
                History = history,
                AnchorBuffer = anchorBuffer,
                Transaction = completedTransaction,
                Primitive = primitive,
                Marker = marker,
                IsApplied = isApplied,
                Caption = label,
                UndoCaption = "Annulla rinomina package: " + label,
                RedoCaption = "Ripeti rinomina package: " + label,
                AnchorPath = anchorPath,
                OriginalAnchorPath = originalAnchorPath,
                CanReplayDetached = canDetached,
                ReplayDetached = replayDetached
            }))
                System.Diagnostics.Trace.WriteLine(
                    "[Java Undo] Impossibile registrare la rinomina package: " + label);
        }
    }

    // Backwards-compatible target type instantiated by JavaCommandsPackage.
    // Its implementation is shared across all Java operations.
    internal sealed class JavaPackageGlobalUndoTarget : JavaGlobalUndoCommandTarget
    {
        internal JavaPackageGlobalUndoTarget(System.IServiceProvider services)
            : base(services) { }
    }
}
