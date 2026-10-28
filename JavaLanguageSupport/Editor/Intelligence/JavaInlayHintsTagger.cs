using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text.RegularExpressions;
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
        // The LSP Type hint is located immediately after the Java var name.
        private static readonly Regex VarBeforeHint = new Regex(
            @"\bvar\s+[\p{L}_$][\p{L}\p{Nd}_$]*\s*$", RegexOptions.Compiled);

        // JDT LS deliberately suppresses var type hints when the initializer is
        // an obvious constructor call (e.g. new Customer(...)).  For that *only*,
        // the constructor's explicit, non-generic type can be displayed safely.
        // No guesses are made for diamond operators, lambdas or method returns.
        // Pack E2: intentionally anchored to an entire Java local-variable
        // declaration. Never guess a type for a general expression.
        // A constructor may have explicit generic arguments (including nested
        // ones), e.g. new ArrayList<String>() or new HashMap<String, List<Integer>>().
        // Diamond <> is deliberately left to semantic JDT LS type inference.
        private static readonly Regex ExplicitConstructorVar = new Regex(
            @"^[ \t]*(?:final[ \t]+)?var[ \t]+(?<name>[\p{L}_$][\p{L}\p{Nd}_$]*)[ \t]*=[ \t]*new[ \t]+(?<type>[\p{L}_$][\p{L}\p{Nd}_$]*(?:\.[\p{L}_$][\p{L}\p{Nd}_$]*)*)[ \t]*(?<generic><[^()\r\n]*>)?[ \t]*\(",
            RegexOptions.Compiled);

        // Pack E2 literal hotfix: "var text = \"...\";" has the certain type String.
        // The pattern is line-anchored and matches complete Java string literals,
        // including escaped quotes. Complex expressions belong to JDT LS.
        // E2 DiamondVar: Java's no-argument java.util.ArrayList diamond
        // constructor infers ArrayList<Object>, NOT ArrayList<String>.
        // Keep this lexical fallback deliberately narrow: arbitrary generic
        // classes and constructors WITH arguments need semantic JDT LS.
        private static readonly Regex ExplicitDiamondArrayListVar = new Regex(
            @"^[ \t]*(?:final[ \t]+)?var[ \t]+(?<name>[\p{L}_$][\p{L}\p{Nd}_$]*)[ \t]*=[ \t]*new[ \t]+(?:java\.util\.)?ArrayList[ \t]*<[ \t]*>[ \t]*\([ \t]*\)[ \t]*;[ \t]*(?://.*)?$",
            RegexOptions.Compiled);

        // E2 ArrayVar: recognize only complete, single-line local Java
        // declarations with an explicit array creation and initializer.
        // A semantic JDT LS hint has priority over this lexical fallback.
        private static readonly Regex ExplicitArrayInitializerVar = new Regex(
            @"^[ \t]*(?:final[ \t]+)?var[ \t]+(?<name>[\p{L}_$][\p{L}\p{Nd}_$]*)[ \t]*=[ \t]*new[ \t]+(?<type>[\p{L}_$][\p{L}\p{Nd}_$]*(?:\.[\p{L}_$][\p{L}\p{Nd}_$]*)*)[ \t]*(?<dims>(?:\[[ \t]*\][ \t]*)+)\{(?<values>.*)\}[ \t]*;[ \t]*(?://.*)?$",
            RegexOptions.Compiled);

        // Check that the initializer is well-formed at the lexical level:
        // nested braces and escaped quotes inside literals are NOT delimiters;
        // ambiguous comment syntax is left to the semantic language server.
        private static bool SafeSingleLineArrayInitializer(string values)
        {
            int braces = 0;
            bool inString = false, inChar = false, escape = false;
            for (int i = 0; i < values.Length; i++)
            {
                char ch = values[i];
                if (inString || inChar)
                {
                    if (escape) { escape = false; continue; }
                    if (ch == '\\') { escape = true; continue; }
                    if (inString && ch == '"') inString = false;
                    else if (inChar && ch == '\'') inChar = false;
                    continue;
                }
                if (ch == '"') { inString = true; continue; }
                if (ch == '\'') { inChar = true; continue; }
                if (ch == '/' && i + 1 < values.Length &&
                    (values[i + 1] == '/' || values[i + 1] == '*')) return false;
                if (ch == '{') braces++;
                else if (ch == '}' && --braces < 0) return false;
            }
            return braces == 0 && !inString && !inChar && !escape;
        }

        private static readonly Regex ExplicitStringLiteralVar = new Regex(
            @"^[ \t]*(?:final[ \t]+)?var[ \t]+(?<name>[\p{L}_$][\p{L}\p{Nd}_$]*)[ \t]*=[ \t]*""(?:\\.|[^""\\\r\n])*""[ \t]*;[ \t]*(?://.*)?$",
            RegexOptions.Compiled);

        private static bool HasValidExplicitGenerics(string args)
        {
            if (args.Length == 0) return true;
            // The fallback cannot infer <> from the left-hand side of var.
            if (args.Length < 3 ||
                string.IsNullOrWhiteSpace(args.Substring(1, args.Length - 2)))
                return false;
            int depth = 0;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == '<') depth++;
                else if (args[i] == '>')
                {
                    if (--depth < 0) return false;
                    if (depth == 0 && i != args.Length - 1) return false;
                }
            }
            return depth == 0;
        }

        internal JavaInlayHintsTagger(IWpfTextView view, ITextDocumentFactoryService documents)
            : base(view, documents) { }

        protected override string MethodName => "textDocument/inlayHint";
        protected override bool NeedsVisibleRange => true;
        // Pack E2: debounce input/scroll while keeping hints responsive.
        protected override int RefreshDelayMilliseconds => 650;
        protected override int WorkspaceIdleThrottleMilliseconds => 1500;
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
            token.ThrowIfCancellationRequested();
            // During initial import JDT LS may temporarily return no hints.
            // The shared E1 retry guard limits retries per document snapshot.
            if ((response == null || response.Count == 0) && JavaWorkspaceActivity.IsBusy)
                RequestBoundedRetry(snapshot);
            if (response == null) response = new JArray();

            // Exactly one adornment per document offset. Native semantic hints
            // always take precedence over the conservative local fallback.
            var occupied = new HashSet<int>();
            foreach (var hint in response.OfType<JObject>().Take(180))
            {
                token.ThrowIfCancellationRequested();
                // Keep parameter hints AND inferred Java var types. Some JDT LS
                // versions omit kind (but provide a label like ": List<String>").
                // Unrelated type hints are not enabled by our client configuration.
                int? hintKind = (int?)hint["kind"];
                if (hintKind.HasValue && hintKind.Value != 1 && hintKind.Value != 2)
                    continue;
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
                // Remove JDT LS's leading colon ONLY for inferred-type
                // hints on `var` declarations, not parameter/lambda hints.
                label = label.Replace("\r", " ").Replace("\n", " ").Trim();
                bool semanticType = hintKind == 1 ||
                    (!hintKind.HasValue && label.StartsWith(":", StringComparison.Ordinal));
                bool varType = semanticType &&
                    VarBeforeHint.IsMatch(row.GetText().Substring(0, character));
                if (varType && label.StartsWith(":", StringComparison.Ordinal))
                    label = label.Substring(1).TrimStart();
                // Parameter labels from JDT LS may include the colon
                // already; normalize only explicit parameter hints.
                if (hintKind == 2 && label.Length != 0 &&
                    !label.EndsWith(":", StringComparison.Ordinal))
                    label += ":";
                if (string.IsNullOrWhiteSpace(label)) continue;
                if (label.Length > 80) label = label.Substring(0, 79) + "…";
                if (!occupied.Add(offset)) continue;
                var ui = MakeLabel(label, varType);
                var span = new SnapshotSpan(snapshot, offset, 0);
                result.Add(new TagSpan<IntraTextAdornmentTag>(span,
                    new IntraTextAdornmentTag(ui, null, PositionAffinity.Predecessor)));
            }
            // Eclipse JDT LS omits "obvious" constructor-initialized local vars.
            // Add a visually distinct hint ONLY for the unambiguous syntax:
            // var example = new Customer(...);
            // Do not duplicate a semantic hint that the server supplied.
            int first = Math.Max(0, snapshot.GetLineFromPosition(Math.Max(0, Math.Min(start, snapshot.Length))).LineNumber);
            int last = Math.Min(snapshot.LineCount - 1,
                snapshot.GetLineFromPosition(Math.Max(0, Math.Min(end, snapshot.Length))).LineNumber);
            int extra = 0;
            for (int n = first; n <= last && extra < 40; n++)
            {
                token.ThrowIfCancellationRequested();
                var line = snapshot.GetLineFromLineNumber(n);
                string lineText = line.GetText();
                if (lineText.Length > 2000) continue;
                // Infer only the unambiguous Java local string literal, e.g.
                // var text = "Hello";  This never changes the Java source.
                // A semantic JDT LS hint always wins at the same position.
                // `var obj = new ArrayList<>();` infers ArrayList<Object>
                // (JDK 21 verified). Do not infer arbitrary `new Foo<>()`
                // or constructors with parameters: JDT LS owns those types.
                var diamondArrayList = ExplicitDiamondArrayListVar.Match(lineText);
                if (diamondArrayList.Success)
                {
                    int diamondPosition = line.Start.Position +
                        diamondArrayList.Groups["name"].Index +
                        diamondArrayList.Groups["name"].Length;
                    if (diamondPosition >= start && diamondPosition <= end &&
                        !occupied.Contains(diamondPosition))
                    {
                        var diamondSpan = new SnapshotSpan(snapshot, diamondPosition, 0);
                        result.Add(new TagSpan<IntraTextAdornmentTag>(diamondSpan,
                            new IntraTextAdornmentTag(MakeLabel("ArrayList<Object>", true),
                                null, PositionAffinity.Predecessor)));
                        occupied.Add(diamondPosition);
                        extra++;
                    }
                    // An authoritative semantic hint, if present, wins;
                    // don't let other lexical fallbacks add duplicates.
                    continue;
                }

                // var arr = new String[] { "x" }; has the definite type String[].
                // Supports nested arrays and quoted/escaped braces within a
                // one-line initializer. Multiline expressions require JDT LS.
                var arrayInitializer = ExplicitArrayInitializerVar.Match(lineText);
                if (arrayInitializer.Success &&
                    SafeSingleLineArrayInitializer(arrayInitializer.Groups["values"].Value))
                {
                    int arrayPosition = line.Start.Position +
                        arrayInitializer.Groups["name"].Index +
                        arrayInitializer.Groups["name"].Length;
                    if (arrayPosition >= start && arrayPosition <= end &&
                        !occupied.Contains(arrayPosition))
                    {
                        string typeName = arrayInitializer.Groups["type"].Value;
                        typeName = typeName.Substring(typeName.LastIndexOf('.') + 1);
                        int dimensions = arrayInitializer.Groups["dims"].Value.Count(ch => ch == '[');
                        string typeLabel = typeName + string.Concat(
                            Enumerable.Repeat("[]", dimensions));
                        var arraySpan = new SnapshotSpan(snapshot, arrayPosition, 0);
                        result.Add(new TagSpan<IntraTextAdornmentTag>(arraySpan,
                            new IntraTextAdornmentTag(MakeLabel(typeLabel, true), null,
                                PositionAffinity.Predecessor)));
                        occupied.Add(arrayPosition);
                        extra++;
                    }
                    continue;
                }

                var stringLiteral = ExplicitStringLiteralVar.Match(lineText);
                if (stringLiteral.Success)
                {
                    int stringPosition = line.Start.Position +
                        stringLiteral.Groups["name"].Index +
                        stringLiteral.Groups["name"].Length;
                    if (stringPosition >= start && stringPosition <= end &&
                        !occupied.Contains(stringPosition))
                    {
                        var stringSpan = new SnapshotSpan(snapshot, stringPosition, 0);
                        result.Add(new TagSpan<IntraTextAdornmentTag>(stringSpan,
                            new IntraTextAdornmentTag(MakeLabel("String", true), null,
                                PositionAffinity.Predecessor)));
                        occupied.Add(stringPosition);
                        extra++;
                    }
                    continue;
                }

                var match = ExplicitConstructorVar.Match(lineText);
                if (!match.Success) continue;
                string genericArguments = match.Groups["generic"].Value;
                if (!HasValidExplicitGenerics(genericArguments)) continue;
                // Comments, complex expressions and anonymous classes are
                // intentionally left to semantic hints from JDT LS.
                if (lineText.IndexOf("/*", StringComparison.Ordinal) >= 0 ||
                    lineText.IndexOf("*/", StringComparison.Ordinal) >= 0 ||
                    lineText.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                string tail = lineText.Substring(match.Index + match.Length);
                if (tail.IndexOf('{') >= 0 || tail.IndexOf('}') >= 0 ||
                    !Regex.IsMatch(tail, @"\)\s*;\s*(?://.*)?$")) continue;
                int position = line.Start.Position + match.Groups["name"].Index +
                    match.Groups["name"].Length;
                if (position < start || position > end || occupied.Contains(position)) continue;
                string qualified = match.Groups["type"].Value;
                // Preserve the explicit type arguments in the visible hint.
                // For example ArrayList<String>, not just ArrayList.
                string label = qualified.Substring(qualified.LastIndexOf('.') + 1)
                    + genericArguments;
                // The type is *not* written into the source buffer.
                var span = new SnapshotSpan(snapshot, position, 0);
                result.Add(new TagSpan<IntraTextAdornmentTag>(span,
                    new IntraTextAdornmentTag(MakeLabel(label, true), null,
                        PositionAffinity.Predecessor)));
                occupied.Add(position);
                extra++;
            }
            return result;
        }

        private static string Label(JToken value)
        {
            if (value == null) return null;
            if (value.Type == JTokenType.String) return (string)value;
            var segments = value as JArray;
            if (segments != null)
                return string.Concat(segments.Select(x =>
                    x.Type == JTokenType.String ? (string)x :
                    x is JObject ? (string)x["value"] ?? "" : ""));
            return null;
        }

        private static Border MakeLabel(string label, bool varType)
        {
            // Keep a small VS-like type chip on the SAME line, with spacing
            // from the identifier and '='; parameter hints keep prior spacing.
            var text = new TextBlock { Text = label, FontSize = 10.5,
                Opacity = 1.0, MaxWidth = 240,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontStyle = varType ? FontStyles.Italic : FontStyles.Normal,
                VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(TextBlock.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            var border = new Border { Child = text, CornerRadius = new CornerRadius(3),
                Padding = varType ? new Thickness(5, 1, 5, 1) : new Thickness(2, 0, 2, 0),
                Margin = varType ? new Thickness(7, 0, 7, 0) : new Thickness(1, 0, 1, 0),
                VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            // Theme resources are dynamic, unlike a fixed semi-transparent
            // brush that can become unreadable after switching VS themes.
            border.SetResourceReference(Border.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            if (varType)
            {
                border.BorderThickness = new Thickness(1);
                border.SetResourceReference(Border.BorderBrushProperty,
                    EnvironmentColors.ToolWindowBorderBrushKey);
            }
            border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return border;
        }
    }
}
