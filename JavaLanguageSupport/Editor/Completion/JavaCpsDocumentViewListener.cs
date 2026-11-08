using System;
using System.ComponentModel.Composition;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace JavaLanguageSupport.Editor.Completion
{
    /// <summary>
    /// F2J.1c: the CPS fallback must open its Java text document independently
    /// of the first Ctrl+J request; otherwise diagnostics and semantic queries
    /// can precede didOpen. Open File / Open Folder are never handled here.
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("java")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class JavaCpsDocumentViewListener : IWpfTextViewCreationListener
    {
        [Import] internal ITextDocumentFactoryService Documents = null;

        public void TextViewCreated(IWpfTextView view)
        {
            if (Documents == null || view == null ||
                !view.TextBuffer.ContentType.IsOfType("java") ||
                !JavaCpsCompletionDocumentSync.Enabled)
                return;

            ITextDocument document;
            if (!Documents.TryGetTextDocument(view.TextBuffer, out document) ||
                !JavaCpsWorkspaceContext.IsLoadedJavaDocument(document.FilePath))
                return;

            var bridge = view.TextBuffer.Properties.GetOrCreateSingletonProperty(
                () => new JavaCpsCompletionDocumentSync(view.TextBuffer));
            bridge.RegisterView(view);
            bridge.BeginInitialSync(view, Documents);
        }
    }
}
