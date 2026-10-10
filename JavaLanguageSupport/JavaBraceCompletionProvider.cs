using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.BraceCompletion;
using Microsoft.VisualStudio.Utilities;

namespace JavaLanguageSupport
{
    /// <summary>
    /// Registra il completamento automatico dei delimitatori nell'editor
    /// di Visual Studio per il content type "java".
    /// Non dipende da JDT LS o dalle richieste LSP.
    /// 
    /// Non modificare direttamente il text buffer: e' il motore nativo
    /// di Visual Studio a creare e gestire la sessione di completamento.
    /// </summary>
    [Export(typeof(IBraceCompletionDefaultProvider))]
    [ContentType("java")]
    [BracePair('{', '}')]
    [BracePair('(', ')')]
    [BracePair('[', ']')]
    internal sealed class JavaBraceCompletionProvider : IBraceCompletionDefaultProvider
    {
    }
}
