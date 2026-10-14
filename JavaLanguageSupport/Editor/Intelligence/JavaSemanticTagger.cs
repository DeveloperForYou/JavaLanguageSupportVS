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
        protected override bool Allowed => base.Allowed && JavaSemanticLegend.Available &&
            FeatureEnabled("disable-semantic-tags.flag");

        protected override async Task<List<ITagSpan<IClassificationTag>>> FetchAsync(
            string uri, ITextSnapshot snapshot, int start, int end, CancellationToken token)
        {
            var tags = new List<ITagSpan<IClassificationTag>>();
            if (snapshot.Length > 1024 * 1024 || classifications == null) return tags;
            JToken response = await JavaLspRpc.RequestAsync(MethodName,
                new { textDocument = new { uri } }, token);
            var data = response == null ? null : response["data"] as JArray;
            if (data == null) return tags;

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
                var kind = ClassificationFor(JavaSemanticLegend.TokenType(typeIndex));
                if (kind == null) continue;
                IClassificationType type = classifications.GetClassificationType(kind);
                if (type == null) continue;
                var span = new SnapshotSpan(snapshot, textLine.Start.Position + col, length);
                tags.Add(new TagSpan<IClassificationTag>(span, new ClassificationTag(type)));
            }
            return tags;
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
                case "namespace":
                case "annotation":
                case "record":
                    return JavaSemanticClassificationNames.Type;
                case "method":
                case "annotationMember":
                case "function":
                case "constructor":
                    return JavaSemanticClassificationNames.Method;
                case "keyword":
                case "modifier":
                    return PredefinedClassificationTypeNames.Keyword;
                case "parameter":
                case "variable":
                case "property":
                case "enumMember":
                case "recordComponent":
                    return PredefinedClassificationTypeNames.Identifier;
                default:
                    return null;
            }
        }
    }
}
