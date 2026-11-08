using System;
using System.ComponentModel.Composition;
using System.IO;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace JavaLanguageSupport.Editor.Diagnostics
{
    /// <summary>
    /// F2J.3: observes editor changes only. Does not participate in IntelliSense,
    /// change the language server protocol, or handle quick fixes.
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("java")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class JavaCpsDiagnosticsLatencyViewListener : IWpfTextViewCreationListener
    {
        [Import] internal ITextDocumentFactoryService Documents = null;

        private static bool Enabled
        {
            get
            {
                try
                {
                    return !File.Exists(Path.Combine(Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                        "JavaLanguageSupportVS", "disable-cps-diagnostics-timing.flag"));
                }
                catch (Exception) { return false; }
            }
        }

        public void TextViewCreated(IWpfTextView view)
        {
            if (!Enabled || view == null || Documents == null ||
                !view.TextBuffer.ContentType.IsOfType("java")) return;
            ITextDocument document;
            if (!Documents.TryGetTextDocument(view.TextBuffer, out document) ||
                !JavaCpsWorkspaceContext.IsLoadedJavaDocument(document.FilePath)) return;
            JavaDiagnosticsLatencyTracker.TrackBuffer(view.TextBuffer,
                JavaLspRpc.FileUri(document));
        }
    }
}
