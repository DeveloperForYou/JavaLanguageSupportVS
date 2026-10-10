using System.ComponentModel.Composition;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Utilities;

namespace JavaLanguageSupport
{
    /// <summary>Associa l'estensione .java al client LSP Java.</summary>
    public static class JavaContentType
    {
#pragma warning disable 0649 // Campi MEF compilati da Visual Studio.
        [Export]
        [Name("java")]
        [BaseDefinition(CodeRemoteContentDefinition.CodeRemoteContentTypeName)]
        internal static ContentTypeDefinition ContentType;

        [Export]
        [FileExtension(".java")]
        [ContentType("java")]
        internal static FileExtensionToContentTypeDefinition FileExtension;
#pragma warning restore 0649
    }
}
