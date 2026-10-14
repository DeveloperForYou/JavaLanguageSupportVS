using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json.Linq;

namespace JavaLanguageSupport.Editor.Intelligence
{
    [Export(typeof(IViewTaggerProvider))]
    [Name("Java JDT LS Parameter Inlay Hints")]
    [ContentType("java")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    [TagType(typeof(IntraTextAdornmentTag))]
    internal sealed class JavaInlayHintsTaggerProvider : IViewTaggerProvider
    {
        [Import] internal ITextDocumentFactoryService Documents = null;

        public ITagger<T> CreateTagger<T>(ITextView view, ITextBuffer buffer) where T : ITag
        {
            var wpf = view as IWpfTextView;
            if (wpf == null || !ReferenceEquals(view.TextBuffer, buffer)) return null;
            return wpf.Properties.GetOrCreateSingletonProperty(() =>
                new JavaInlayHintsTagger(wpf, Documents)) as ITagger<T>;
        }
    }

    internal sealed class JavaInlayHintsTagger : JavaLspTaggerBase<IntraTextAdornmentTag>
    {
        internal JavaInlayHintsTagger(IWpfTextView view, ITextDocumentFactoryService documents)
            : base(view, documents) { }

        protected override string MethodName => "textDocument/inlayHint";
        protected override bool NeedsVisibleRange => true;
        protected override bool Allowed => base.Allowed && FeatureEnabled("disable-inlay-hints.flag");

        protected override async Task<List<ITagSpan<IntraTextAdornmentTag>>> FetchAsync(
            string uri, ITextSnapshot snapshot, int start, int end, CancellationToken token)
        {
            var result = new List<ITagSpan<IntraTextAdornmentTag>>();
            var parameters = new
            {
                textDocument = new { uri },
                range = new { start = JavaLspRpc.Position(snapshot, start),
                              end = JavaLspRpc.Position(snapshot, end) }
            };
            var response = await JavaLspRpc.RequestAsync(MethodName, parameters, token) as JArray;
            if (response == null) return result;

            var distinct = new HashSet<string>(StringComparer.Ordinal);
            foreach (var hint in response.OfType<JObject>().Take(180))
            {
                // Pack 3 renders only PARAMETER hints: type hints can appear
                // at line ends and are not part of the parameter/value pair.
                int? hintKind = (int?)hint["kind"];
                if (hintKind.HasValue && hintKind.Value != 2) continue;
                var position = hint["position"];
                if (position == null) continue;
                int line = (int?)position["line"] ?? -1;
                int character = (int?)position["character"] ?? -1;
                if (line < 0 || line >= snapshot.LineCount) continue;
                var row = snapshot.GetLineFromLineNumber(line);
                if (character < 0 || character > row.Length) continue;
                int offset = row.Start.Position + character;
                if (offset < start || offset > end) continue;
                string label = Label(hint["label"]);
                if (string.IsNullOrWhiteSpace(label)) continue;
                if (label.Length > 80) label = label.Substring(0, 79) + "…";
                if (!distinct.Add(offset + ":" + label)) continue;
                var ui = MakeLabel(label);
                var span = new SnapshotSpan(snapshot, offset, 0);
                result.Add(new TagSpan<IntraTextAdornmentTag>(span,
                    new IntraTextAdornmentTag(ui, null, PositionAffinity.Predecessor)));
            }
            return result;
        }

        private static string Label(JToken value)
        {
            if (value == null) return null;
            if (value.Type == JTokenType.String) return (string)value;
            var segments = value as JArray;
            if (segments != null)
                return string.Concat(segments.Select(x => (string)x["value"] ?? ""));
            return null;
        }

        private static Border MakeLabel(string label)
        {
            // IntraTextAdornmentTag remains INLINE, at JDT LS's argument position.
            // In particular, CodeLens uses InterLineAdornmentTag, not this tag.
            var text = new TextBlock { Text = label, FontSize = 10.5,
                TextWrapping = TextWrapping.NoWrap,
                FontStyle = FontStyles.Italic, VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(TextBlock.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            var border = new Border { Child = text, CornerRadius = new CornerRadius(3),
                Padding = new Thickness(2, 0, 2, 0), Margin = new Thickness(1, 0, 1, 0),
                VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            border.SetResourceReference(Border.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return border;
        }
    }
}
