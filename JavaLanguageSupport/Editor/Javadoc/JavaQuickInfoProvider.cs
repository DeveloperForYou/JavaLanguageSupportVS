using System;
using System.ComponentModel.Composition;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json.Linq;

namespace JavaLanguageSupport.Editor.Javadoc
{
    // Fallback nativo per Quick Info. Non sostituisce le altre funzioni LSP dell'IDE.
    [Export(typeof(IAsyncQuickInfoSourceProvider))]
    [Name("Java JDT LS Quick Info")]
    [ContentType("java")]
    internal sealed class JavaQuickInfoProvider : IAsyncQuickInfoSourceProvider
    {
        [Import] internal ITextDocumentFactoryService DocumentFactory = null;

        public IAsyncQuickInfoSource TryCreateQuickInfoSource(ITextBuffer textBuffer)
            => new JavaQuickInfoSource(textBuffer, DocumentFactory);
    }

    internal sealed class JavaQuickInfoSource : IAsyncQuickInfoSource
    {
        private readonly ITextBuffer buffer;
        private readonly ITextDocumentFactoryService documents;

        internal JavaQuickInfoSource(ITextBuffer buffer, ITextDocumentFactoryService documents)
        {
            this.buffer = buffer;
            this.documents = documents;
        }

        public async Task<QuickInfoItem> GetQuickInfoItemAsync(IAsyncQuickInfoSession session,
            CancellationToken cancellationToken)
        {
            if (!JavaLspRpc.Connected) return null;
            var snapshot = buffer.CurrentSnapshot;
            var trigger = session.GetTriggerPoint(snapshot);
            if (!trigger.HasValue) return null;
            ITextDocument document;
            if (!documents.TryGetTextDocument(buffer, out document)) return null;
            string uri = JavaLspRpc.FileUri(document);
            if (uri == null || snapshot.Length == 0) return null;

            int offset = Math.Min(trigger.Value.Position, snapshot.Length - 1);
            if (!IsIdentifier(snapshot[offset]) && offset > 0 && IsIdentifier(snapshot[offset - 1]))
                offset--;
            if (!IsIdentifier(snapshot[offset])) return null;
            int start = offset, end = offset + 1;
            while (start > 0 && IsIdentifier(snapshot[start - 1])) start--;
            while (end < snapshot.Length && IsIdentifier(snapshot[end])) end++;
            var parameters = new
            {
                textDocument = new { uri },
                position = JavaLspRpc.Position(snapshot, offset)
            };
            JToken result = await JavaLspRpc.RequestAsync("textDocument/hover", parameters,
                cancellationToken).ConfigureAwait(false);
            string text = FormatHover(result);
            if (string.IsNullOrWhiteSpace(text)) return null;
            var span = snapshot.CreateTrackingSpan(new Span(start, end - start), SpanTrackingMode.EdgeInclusive);
            return new QuickInfoItem(span, text);
        }

        private static bool IsIdentifier(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

        private static string FormatHover(JToken response)
        {
            if (response == null || response.Type == JTokenType.Null) return null;
            var contents = (response as JObject)?["contents"];
            if (contents == null) return null;
            // MarkupContent, MarkedString, lista di MarkedString e stringa semplice.
            var result = new StringBuilder();
            if (contents is JArray)
            {
                foreach (var part in (JArray)contents) AppendPart(result, part);
            }
            else AppendPart(result, contents);
            return result.ToString().Trim();
        }

        private static void AppendPart(StringBuilder result, JToken part)
        {
            if (part == null || part.Type == JTokenType.Null) return;
            string value = part.Type == JTokenType.String ? (string)part : (string)part["value"];
            if (string.IsNullOrWhiteSpace(value)) return;
            if (result.Length > 0) result.AppendLine().AppendLine();
            result.Append(value); // Markdown testuale; nessuna interpretazione di HTML remoto.
        }

        public void Dispose() { }
    }
}
