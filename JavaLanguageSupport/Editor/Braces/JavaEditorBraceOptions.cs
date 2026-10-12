using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace JavaLanguageSupport.Editor.Braces
{
    /// <summary>
    /// Mantiene abilitato il completamento nativo delle parentesi soltanto
    /// nelle viste modificabili Java. Non cambia le opzioni di C# o altri file.
    /// Il fallback TypeChar rimane in JavaAutoPairCommandHandler.
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("java")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class JavaEditorBraceOptions : IWpfTextViewCreationListener
    {
        public void TextViewCreated(IWpfTextView textView)
        {
            if (textView == null) return;
            try
            {
                if (!textView.Options.GetOptionValue(
                    DefaultTextViewOptions.BraceCompletionEnabledOptionId))
                {
                    textView.Options.SetOptionValue(
                        DefaultTextViewOptions.BraceCompletionEnabledOptionId, true);
                }
            }
            catch (Exception)
            {
                // Se questa opzione non e' disponibile, rimane il fallback
                // JavaAutoPairCommandHandler; non bloccare l'editor Java.
            }
        }
    }
}
