using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace JavaLanguageSupport
{
    /// <summary>
    /// Diagnostica temporanea: registra il content type realmente usato dall'editor.
    /// Non modifica buffer, tasti, IntelliSense o JDT LS.
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class JavaEditorBraceProbe : IWpfTextViewCreationListener
    {
        [Import]
        internal ITextDocumentFactoryService TextDocumentFactory { get; set; }

        public void TextViewCreated(IWpfTextView textView)
        {
            try
            {
                if (textView == null)
                    return;

                var contentType = textView.TextBuffer.ContentType;
                ITextDocument document;
                string filePath = TextDocumentFactory != null &&
                    TextDocumentFactory.TryGetTextDocument(textView.TextBuffer, out document)
                    ? document.FilePath : "(documento non disponibile)";

                bool javaFile = filePath.EndsWith(".java", StringComparison.OrdinalIgnoreCase);
                bool csharpFile = filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
                bool javaType = contentType.IsOfType("java") ||
                    contentType.TypeName.IndexOf("java", StringComparison.OrdinalIgnoreCase) >= 0;

                if (!javaFile && !csharpFile && !javaType)
                    return;

                string logDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "JavaLanguageSupportVS", "logs");
                Directory.CreateDirectory(logDirectory);
                string logPath = Path.Combine(logDirectory, "editor-braces.log");

                // Controlla se l'editor Java abilita davvero il motore di completamento.
                // Se disabilitato soltanto in questa vista Java, lo riabilitiamo localmente.
                string autoBrace = "sconosciuto";
                try
                {
                    bool before = textView.Options.GetOptionValue(
                        DefaultTextViewOptions.BraceCompletionEnabledOptionId);
                    autoBrace = before.ToString();
                    if (javaFile && javaType && !before)
                    {
                        textView.Options.SetOptionValue(
                            DefaultTextViewOptions.BraceCompletionEnabledOptionId, true);
                        autoBrace += " -> " + textView.Options.GetOptionValue(
                            DefaultTextViewOptions.BraceCompletionEnabledOptionId);
                    }
                }
                catch (Exception ex)
                {
                    autoBrace = "ERRORE: " + ex.GetType().Name + ": " + ex.Message;
                }

                bool textMateBrace = contentType.IsOfType(
                    CodeRemoteContentDefinition.CodeRemoteTextMateBraceCompletionTypeName);
                bool codeRemote = contentType.IsOfType(
                    CodeRemoteContentDefinition.CodeRemoteContentTypeName);

                string info = string.Format(
                    "{0:O} | File={1} | ContentType={2} | IsOfType(java)={3} | " +
                    "BaseTypes={4} | Editable={5} | Document={6} | " +
                    "AutoBrace={7} | TextMateBraceType={8} | CodeRemoteType={9}{10}",
                    DateTime.Now,
                    filePath,
                    contentType.TypeName,
                    contentType.IsOfType("java"),
                    string.Join(", ", contentType.BaseTypes.Select(x => x.TypeName)),
                    textView.Roles.Contains(PredefinedTextViewRoles.Editable),
                    textView.Roles.Contains(PredefinedTextViewRoles.Document),
                    autoBrace, textMateBrace, codeRemote,
                    Environment.NewLine);
                File.AppendAllText(logPath, info);
            }
            catch
            {
                // La diagnostica non deve mai interrompere l'apertura dell'editor.
            }
        }
    }
}
