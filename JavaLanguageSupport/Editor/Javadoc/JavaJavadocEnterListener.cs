using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace JavaLanguageSupport.Editor.Javadoc
{
    /// <summary>
    /// WPF-key fallback for Enter when the VS editor command chain does not
    /// invoke ReturnKeyCommandArgs for an LSP content type. Only intercepts
    /// Enter INSIDE a Java block comment; normal Java editing is unchanged.
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("java")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class JavaJavadocEnterListener : IWpfTextViewCreationListener
    {
        public void TextViewCreated(IWpfTextView view)
        {
            if (view == null)
                return;

            WriteLog("Listener collegato: " + view.TextBuffer.ContentType.TypeName);
            view.VisualElement.PreviewKeyDown += (sender, e) =>
            {
                if (e.Handled || (e.Key != Key.Return && e.Key != Key.Enter))
                    return;
                if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0)
                    return;
                try
                {
                    WriteLog("Enter ricevuto");
                    if (TryHandleEnter(view))
                        e.Handled = true;
                    else
                        WriteLog("Enter delegato all'editor nativo");
                }
                catch (Exception ex)
                {
                    // Do not swallow Enter when an unexpected editor state
                    // occurs: allow Visual Studio's native Enter handler.
                    WriteLog("Errore Enter: " + ex.GetType().Name + ": " + ex.Message);
                }
            };
        }

        private static bool TryHandleEnter(IWpfTextView view)
        {
            if (!view.Selection.IsEmpty || view.TextBuffer == null || view.TextBuffer.ContentType == null)
                return false;

            ITextBuffer buffer = view.TextBuffer;
            ITextSnapshot snapshot = buffer.CurrentSnapshot;
            int position = view.Caret.Position.BufferPosition.Position;
            if (position < 0 || position > snapshot.Length || buffer.IsReadOnly(position))
                return false;

            string indent;
            bool javadoc;
            if (!JavaJavadocCommandHandler.TryGetOpenBlockComment(
                    snapshot, position, out indent, out javadoc))
                return false;

            ITextSnapshotLine line = snapshot.GetLineFromPosition(position);
            string left = snapshot.GetText(line.Start.Position, position - line.Start.Position);
            string right = snapshot.GetText(position, line.End.Position - position);
            string newline = DetectNewline(snapshot, line);

            // Don't insert a '*' after the closing delimiter itself.
            if (Regex.IsMatch(line.GetText(), @"^[ \t]*\*/[ \t]*$"))
                return false;

            // A newly typed /**| or /**|*/ immediately above a declaration.
            if (javadoc && Regex.IsMatch(left, @"^[ \t]*/\*\*$") &&
                (right.Trim().Length == 0 || right.Trim() == "*/"))
            {
                var tags = JavaDocSignatureTags.FromFollowingDeclaration(snapshot, line.LineNumber + 1);
                var replacement = new StringBuilder();
                replacement.Append(indent).Append("/**").Append(newline)
                    .Append(indent).Append(" * ");
                foreach (string tag in tags)
                    replacement.Append(newline).Append(indent).Append(" * ").Append(tag);
                replacement.Append(newline).Append(indent).Append(" */");

                ITextSnapshot updated = buffer.Replace(new Span(line.Start.Position, line.Length),replacement.ToString());
                int newCaret = line.Start.Position + indent.Length + 3 + newline.Length + indent.Length + 3;
                view.Caret.MoveTo(new SnapshotPoint(updated, newCaret));
                WriteLog("Javadoc creato con " + tags.Count + " tag");
                return true;
            }

            // The normal multiline-comment case: continue with ' * '.
            // When Enter splits /*|*/, move */ to its own closing line.
            bool closingOnRight = right.Trim() == "*/";
            string insertion = newline + indent + " * ";
            if (closingOnRight)
                insertion += newline + indent;

            ITextSnapshot changed = buffer.Insert(position, insertion);
            int cursor = position + newline.Length + indent.Length + 3;
            view.Caret.MoveTo(new SnapshotPoint(changed, cursor));
            WriteLog("Asterisco inserito (chiusura a destra=" + closingOnRight + ")");
            return true;
        }

        private static string DetectNewline(ITextSnapshot snapshot, ITextSnapshotLine line)
        {
            if (line.LineBreakLength != 0)
                return line.GetLineBreakText();
            if (line.LineNumber > 0)
            {
                ITextSnapshotLine previous = snapshot.GetLineFromLineNumber(line.LineNumber - 1);
                if (previous.LineBreakLength != 0)
                    return previous.GetLineBreakText();
            }
            return Environment.NewLine;
        }

        private static void WriteLog(string message)
        {
            try
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "JavaLanguageSupportVS", "logs");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "editor-javadoc.log"),
                    DateTime.Now.ToString("O") + " | [PreviewKeyDown] " + message + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
