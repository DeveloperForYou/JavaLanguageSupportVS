using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json.Linq;

namespace JavaLanguageSupport.Editor.Javadoc
{
    // Quick Info nativo per i file Java; JDT LS resta l'unica fonte della documentazione.
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

        public async Task<QuickInfoItem> GetQuickInfoItemAsync(
            IAsyncQuickInfoSession session, CancellationToken cancellationToken)
        {
            // Pack A2: do not start a hover for a disposed/incomplete view.
            if (session == null || buffer == null || documents == null ||
                !JavaLspRpc.Connected || cancellationToken.IsCancellationRequested)
                return null;

            var snapshot = buffer.CurrentSnapshot;
            var trigger = session.GetTriggerPoint(snapshot);
            if (!trigger.HasValue || snapshot.Length == 0) return null;

            ITextDocument document;
            if (!documents.TryGetTextDocument(buffer, out document)) return null;
            string uri = JavaLspRpc.FileUri(document);
            if (string.IsNullOrWhiteSpace(uri)) return null;

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

            // Pack A2: bound the hover request. Native VS is still responsible
            // for triggering/dismissing Quick Info and managing the LSP client.
            JToken hover;
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                limit.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    hover = await JavaLspRpc.RequestAsync(
                        "textDocument/hover", parameters, limit.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return null; // Server busy: a later hover can retry.
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Do not attach old Javadoc to a symbol after the user has edited it.
            if (!ReferenceEquals(snapshot, buffer.CurrentSnapshot) || !JavaLspRpc.Connected)
                return null;

            var span = snapshot.CreateTrackingSpan(
                new Span(start, end - start), SpanTrackingMode.EdgeInclusive);

            // Avoid formatting the same Javadoc twice. Use the native classified
            // fallback only in simple mode or if no rich Markdown is available.
            // WPF controls are created later on the VS UI thread by MEF.
            if (!IsSimpleMode())
            {
                string markdown = JavaHoverLspMarkdown.Read(hover);
                if (!string.IsNullOrWhiteSpace(markdown))
                    return new QuickInfoItem(span, new JavaHoverWpfContent(markdown));
            }
            ClassifiedTextElement content = JavaHoverFormatter.Format(hover);
            return content == null ? null : new QuickInfoItem(span, content);
        }

        // Creare questo file vuoto per ripristinare immediatamente il popup senza scrollbar:
        // %LOCALAPPDATA%\JavaLanguageSupportVS\disable-scroll-quickinfo.flag
        private static bool IsSimpleMode()
        {
            try
            {
                string root = Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData), "JavaLanguageSupportVS");
                return File.Exists(Path.Combine(root, "disable-scroll-quickinfo.flag"));
            }
            catch { return false; }
        }

        private static bool IsIdentifier(char c)
            => char.IsLetterOrDigit(c) || c == '_' || c == '$';

        public void Dispose() { }
    }

    internal static class JavaHoverFormatter
    {
        private const string Plain = PredefinedClassificationTypeNames.Text;
        private const string Keyword = PredefinedClassificationTypeNames.Keyword;
        private const string Type = PredefinedClassificationTypeNames.Type;
        private const string Identifier = PredefinedClassificationTypeNames.Identifier;
        private const string Method = PredefinedClassificationTypeNames.Method;
        private const string Punctuation = PredefinedClassificationTypeNames.Punctuation;
        private const int MaximumCharacters = 200000;
        private const int MaximumLines = 4000;

        private static readonly Regex MarkdownInline = new Regex(
            @"\*\*[^*\r\n]+\*\*|`[^`\r\n]+`|\[[^\]\r\n]+\]\([^)\r\n]+\)|\*[^*\r\n]+\*",
            RegexOptions.Compiled);
        private static readonly Regex SignatureTokens = new Regex(
            @"[A-Za-z_$][A-Za-z0-9_$]*|\s+|.", RegexOptions.Compiled);
        private static readonly Regex SourceLine = new Regex(
            @"^\s*(?:[-*>]\s*)?(?:\*\*)?(?:Source|Sorgente|Origine)(?:\*\*)?\s*:",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex JavaKeywords = new Regex(
            @"^(?:public|protected|private|static|final|abstract|synchronized|native|default|void|class|interface|enum|record|extends|implements|throws|byte|short|int|long|float|double|boolean|char|var)$",
            RegexOptions.Compiled);

        internal static ClassifiedTextElement Format(JToken hover)
        {
            string markdown = GetMarkdown(hover);
            if (string.IsNullOrWhiteSpace(markdown)) return null;

            markdown = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
            bool truncated = markdown.Length > MaximumCharacters;
            if (truncated) markdown = markdown.Substring(0, MaximumCharacters);

            var runs = new List<ClassifiedTextRun>();
            bool firstLine = true;
            bool blankPending = false;
            int displayed = 0;

            foreach (string sourceLine in markdown.Split('\n'))
            {
                if (displayed >= MaximumLines) { truncated = true; break; }
                string line = NormalizeHtml(sourceLine.Trim());
                if (line.StartsWith("```", StringComparison.Ordinal) || SourceLine.IsMatch(line))
                    continue;

                if (line.Length == 0)
                {
                    if (!firstLine) blankPending = true;
                    continue;
                }

                if (Regex.IsMatch(line, @"^(?:---+|___+)\s*$")) continue;
                if (!firstLine) Add(runs, Plain, blankPending ? "\n\n" : "\n");
                blankPending = false;
                displayed++;

                string section = SectionTitle(line);
                if (section != null)
                {
                    Add(runs, Plain, section, ClassifiedTextRunStyle.Bold);
                }
                else if (firstLine && LooksLikeJavaSignature(line))
                {
                    AppendSignature(runs, StripFencedInlineCode(line));
                }
                else if (line.StartsWith("@param ", StringComparison.Ordinal))
                {
                    AppendParameter(runs, line.Substring(7));
                }
                else if (line.StartsWith("@return", StringComparison.Ordinal))
                {
                    Add(runs, Plain, "Restituisce: ", ClassifiedTextRunStyle.Bold);
                    AppendInline(runs, line.Substring(7).Trim());
                }
                else if (line.StartsWith("@throws ", StringComparison.Ordinal) ||
                         line.StartsWith("@exception ", StringComparison.Ordinal))
                {
                    AppendParameter(runs, line.Substring(line.IndexOf(' ') + 1));
                }
                else if (Regex.IsMatch(line, @"^[-*+]\s+"))
                {
                    Add(runs, Plain, "  •  ");
                    AppendInline(runs, line.Substring(2).Trim());
                }
                else
                {
                    AppendInline(runs, line);
                }
                firstLine = false;
            }

            if (truncated)
            {
                if (runs.Count > 0) Add(runs, Plain, "\n");
                Add(runs, Plain, "… Documentazione abbreviata", ClassifiedTextRunStyle.Italic);
            }
            return runs.Count == 0 ? null : new ClassifiedTextElement(runs);
        }

        private static string GetMarkdown(JToken hover)
        {
            if (!(hover is JObject hoverObject)) return null;
            JToken contents = hoverObject["contents"];
            if (contents == null || contents.Type == JTokenType.Null) return null;

            var result = new StringBuilder();
            if (contents is JArray array)
            {
                foreach (JToken part in array) AppendPart(result, part);
            }
            else AppendPart(result, contents);
            return result.ToString().Trim();
        }

        private static void AppendPart(StringBuilder builder, JToken part)
        {
            if (part == null || part.Type == JTokenType.Null) return;
            JObject partObject = part as JObject;
            string value = part.Type == JTokenType.String
                ? (string)part
                : partObject == null ? null : (string)partObject["value"];
            if (string.IsNullOrWhiteSpace(value)) return;
            if (builder.Length > 0) builder.AppendLine().AppendLine();
            builder.Append(value);
        }

        private static string NormalizeHtml(string line)
        {
            // Solo tag di formattazione: non eseguiamo HTML o contenuti web provenienti dal server.
            line = Regex.Replace(line, @"<br\s*/?>", " ", RegexOptions.IgnoreCase);
            line = Regex.Replace(line, @"</?(?:p|div|code|pre|em|strong|ul|ol|li|span)\b[^>]*>",
                string.Empty, RegexOptions.IgnoreCase);
            return WebUtility.HtmlDecode(line).Trim();
        }

        private static string SectionTitle(string line)
        {
            string header = line.Trim().Trim('*', '#', ':', ' ').ToLowerInvariant();
            switch (header)
            {
                case "parameters": case "params": case "parametri": return "Parametri";
                case "type parameters": case "parametri di tipo": return "Parametri di tipo";
                case "returns": case "return": case "restituisce": return "Restituisce";
                case "throws": case "exceptions": case "eccezioni": return "Eccezioni";
                case "deprecated": case "deprecato": return "Deprecato";
                case "see also": case "vedi anche": return "Vedi anche";
                default: return null;
            }
        }

        private static bool LooksLikeJavaSignature(string line)
        {
            return (line.IndexOf('(') >= 0 && line.IndexOf(')') > line.IndexOf('(')) ||
                Regex.IsMatch(line, @"^(?:class|interface|enum|record)\s+\w+");
        }

        private static string StripFencedInlineCode(string line)
        {
            return line.Trim().Trim('`');
        }

        private static void AppendSignature(List<ClassifiedTextRun> runs, string signature)
        {
            MatchCollection tokens = SignatureTokens.Matches(signature);
            for (int i = 0; i < tokens.Count; i++)
            {
                string token = tokens[i].Value;
                string category = Plain;
                if (char.IsLetter(token[0]) || token[0] == '_' || token[0] == '$')
                {
                    if (JavaKeywords.IsMatch(token)) category = Keyword;
                    else if (i + 1 < tokens.Count && tokens[i + 1].Value == "(") category = Method;
                    else if (char.IsUpper(token[0])) category = Type;
                    else category = Identifier;
                }
                else if (!char.IsWhiteSpace(token[0])) category = Punctuation;

                Add(runs, category, token,
                    category == Method ? ClassifiedTextRunStyle.Bold : ClassifiedTextRunStyle.Plain);
            }
        }

        private static void AppendParameter(List<ClassifiedTextRun> runs, string line)
        {
            Add(runs, Plain, "  •  ");
            string[] parts = line.Trim().Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;
            Add(runs, Identifier, parts[0], ClassifiedTextRunStyle.Bold);
            if (parts.Length == 2)
            {
                Add(runs, Plain, "  —  ");
                AppendInline(runs, parts[1]);
            }
        }

        private static void AppendInline(List<ClassifiedTextRun> runs, string value)
        {
            int start = 0;
            foreach (Match match in MarkdownInline.Matches(value))
            {
                if (match.Index > start) Add(runs, Plain, value.Substring(start, match.Index - start));
                string token = match.Value;
                if (token.StartsWith("**", StringComparison.Ordinal))
                    Add(runs, Identifier, token.Substring(2, token.Length - 4), ClassifiedTextRunStyle.Bold);
                else if (token[0] == '`')
                    Add(runs, Type, token.Substring(1, token.Length - 2));
                else if (token[0] == '[')
                    Add(runs, Plain, token.Substring(1, token.IndexOf(']') - 1));
                else if (token[0] == '*')
                    Add(runs, Plain, token.Substring(1, token.Length - 2), ClassifiedTextRunStyle.Italic);
                start = match.Index + match.Length;
            }
            if (start < value.Length) Add(runs, Plain, value.Substring(start));
        }

        private static void Add(List<ClassifiedTextRun> runs, string classification,
            string text, ClassifiedTextRunStyle style = ClassifiedTextRunStyle.Plain)
        {
            if (!string.IsNullOrEmpty(text))
                runs.Add(new ClassifiedTextRun(classification, text, style));
        }
    }
}
