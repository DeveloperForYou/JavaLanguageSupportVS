// Registration contract for native-linked Java Undo/Redo operations.
// Implementations must validate filesystem state before detached replay.
using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Operations;

namespace JavaLanguageSupport.Editor.Undo
{
    internal sealed class JavaGlobalUndoOperation
    {
        // Set after ITextUndoTransaction.Complete and DTE.UndoContext.Close.
        internal ITextUndoHistory History;
        internal ITextUndoTransaction Transaction;
        internal ITextUndoPrimitive Primitive;
        internal ITextBuffer AnchorBuffer;
        // Unique token embedded in the native OLE undo description: [Marker].
        internal string Marker;
        internal Func<bool> IsApplied;
        internal string Caption;
        internal string UndoCaption;
        internal string RedoCaption;
        internal string AnchorPath;
        internal string OriginalAnchorPath;
        // Optional. If another linked editor buffer closes, its native
        // history is no longer safe for replay by a global target.
        internal IEnumerable<string> RequiredOpenDocumentPaths;
        // Optional as a PAIR. If omitted, closing the anchor disables global
        // replay; it never causes unsafe replay on disposed document buffers.
        // redo=true for replay, false for undo. Validate direction and files.
        internal Func<bool, bool> CanReplayDetached;
        internal Action<bool> ReplayDetached;
    }
}
