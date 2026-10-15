using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json.Linq;

namespace JavaLanguageSupport.Editor.Intelligence
{
    [Export(typeof(IViewTaggerProvider))]
    [Name("Java JDT LS Semantic Classifications")]
    [ContentType("java")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    [TagType(typeof(IClassificationTag))]
    internal sealed class JavaSemanticTaggerProvider : IViewTaggerProvider
    {
        [Import] internal ITextDocumentFactoryService Documents = null;
        [Import] internal IClassificationTypeRegistryService Registry = null;

        public ITagger<T> CreateTagger<T>(ITextView view, ITextBuffer buffer) where T : ITag
        {
            var wpf = view as IWpfTextView;
            if (wpf == null || !ReferenceEquals(view.TextBuffer, buffer)) return null;
            return wpf.Properties.GetOrCreateSingletonProperty(() =>
                new JavaSemanticTagger(wpf, Documents, Registry)) as ITagger<T>;
        }
    }

    internal sealed class JavaSemanticTagger : JavaLspTaggerBase<IClassificationTag>
    {
        private readonly IClassificationTypeRegistryService classifications;

        internal JavaSemanticTagger(IWpfTextView view, ITextDocumentFactoryService documents,
            IClassificationTypeRegistryService registry) : base(view, documents)
        {
            classifications = registry;
        }

        protected override string MethodName => "textDocument/semanticTokens/full";
        // Even if JDT LS does not advertise semanticTokens, Java identifiers
        // should retain useful, readable colors via the lexical fallback.
        protected override bool Allowed => FeatureEnabled("disable-semantic-tags.flag");

        protected override async Task<List<ITagSpan<IClassificationTag>>> FetchAsync(
            string uri, ITextSnapshot snapshot, int start, int end, CancellationToken token)
        {
            var tags = new List<ITagSpan<IClassificationTag>>();
            if (snapshot.Length > 1024 * 1024 || classifications == null) return tags;
            var semanticPositions = new HashSet<int>();
            if (JavaLspRpc.Connected && JavaSemanticLegend.Available)
            {
                JToken response = await JavaLspRpc.RequestAsync(MethodName,
                    new { textDocument = new { uri } }, token);
                var data = response == null ? null : response["data"] as JArray;
                if (data != null)
                {
                    int line = 0, col = 0;
                    int count = Math.Min(data.Count / 5, 25000);
                    for (int i = 0; i < count; i++)
                    {
                        int j = i * 5;
                        int lineDelta = (int)data[j];
                        int charDelta = (int)data[j + 1];
                        int length = (int)data[j + 2];
                        int typeIndex = (int)data[j + 3];
                        if (lineDelta < 0 || charDelta < 0 || length < 1) continue;
                        line += lineDelta;
                        col = lineDelta == 0 ? col + charDelta : charDelta;
                        if (line < 0 || line >= snapshot.LineCount) continue;
                        var textLine = snapshot.GetLineFromLineNumber(line);
                        if (col < 0 || col + length > textLine.Length) continue;
                        string kind = ClassificationFor(JavaSemanticLegend.TokenType(typeIndex));
                        if (kind == null) continue;
                        // Package/import directives are handled by the lexical
                        // scanner: JDT LS may label all segments as generic types,
                        // hiding distinctions such as java.util.List.
                        if (IsPackageOrImportLine(textLine)) continue;
                        IClassificationType type = classifications.GetClassificationType(kind);
                        if (type == null) continue;
                        int offset = textLine.Start.Position + col;
                        semanticPositions.Add(offset);
                        var span = new SnapshotSpan(snapshot, offset, length);
                        tags.Add(new TagSpan<IClassificationTag>(span, new ClassificationTag(type)));
                    }
                }
            }
            // Semantic data can be incomplete or entirely unsupported by JDT LS.
            // Fill uncovered identifiers without asking the server to reindex.
            token.ThrowIfCancellationRequested();
            tags.AddRange(JavaLexicalSemanticFallback.Scan(snapshot, classifications,
                semanticPositions));
            return tags;
        }

        private static bool IsPackageOrImportLine(ITextSnapshotLine line)
        {
            string text = line.GetText().TrimStart();
            return DirectivePrefix(text, "package") || DirectivePrefix(text, "import");
        }

        private static bool DirectivePrefix(string text, string keyword)
        {
            return text.Length > keyword.Length &&
                text.StartsWith(keyword, StringComparison.Ordinal) &&
                char.IsWhiteSpace(text[keyword.Length]);
        }

        private static string ClassificationFor(string tokenType)
        {
            switch (tokenType)
            {
                case "class":
                case "interface":
                case "enum":
                case "type":
                case "typeParameter":
                case "record":
                    return JavaSemanticClassificationNames.Type;
                case "annotation":
                    return JavaSemanticClassificationNames.Annotation;
                case "namespace":
                    return JavaSemanticClassificationNames.Namespace;
                case "method":
                case "annotationMember":
                case "function":
                case "constructor":
                    return JavaSemanticClassificationNames.Method;
                case "keyword":
                case "modifier":
                    return PredefinedClassificationTypeNames.Keyword;
                case "parameter":
                    return JavaSemanticClassificationNames.Parameter;
                case "variable":
                case "property":
                case "enumMember":
                case "recordComponent":
                    return JavaSemanticClassificationNames.Variable;
                default:
                    return null;
            }
        }
    }
}
