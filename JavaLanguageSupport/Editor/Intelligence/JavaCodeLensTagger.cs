using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using JavaLanguageSupport.Editor.Navigation;
using JavaLanguageSupport.Editor.Refactoring;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json.Linq;

namespace JavaLanguageSupport.Editor.Intelligence
{
    // InterLineAdornmentTag reserves a separate row ABOVE the Java declaration.
    // IntraTextAdornmentTag (Pack 2) used to insert CodeLens inside the method line.
    [Export(typeof(IViewTaggerProvider))]
    [Name("Java JDT LS CodeLens")]
    [ContentType("java")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    [TagType(typeof(InterLineAdornmentTag))]
    internal sealed class JavaCodeLensTaggerProvider : IViewTaggerProvider
    {
        [Import] internal ITextDocumentFactoryService Documents = null;

        public ITagger<T> CreateTagger<T>(ITextView view, ITextBuffer buffer) where T : ITag
        {
            var wpf = view as IWpfTextView;
            if (wpf == null || !ReferenceEquals(view.TextBuffer, buffer)) return null;
            return wpf.Properties.GetOrCreateSingletonProperty(() =>
                new JavaCodeLensTagger(wpf, Documents)) as ITagger<T>;
        }
    }

    internal sealed class JavaCodeLensTagger : JavaLspTaggerBase<InterLineAdornmentTag>
    {
        private sealed class Link
        {
            internal string Title;
            internal string Kind;
            internal bool Field;
            internal int Offset;
            internal string Uri;
            internal ITextSnapshot Snapshot;
        }

        internal JavaCodeLensTagger(IWpfTextView view, ITextDocumentFactoryService documents)
            : base(view, documents) { }

        protected override string MethodName => "textDocument/codeLens";
        // Keep CodeLens above declarations stable while typing. Refresh
        // after a short idle period, and promptly after structural edits.
        // Workspace-idle signals also refresh counters when OTHER Java
        // files change (for example after a multi-file refactoring).
        protected override bool PreserveAdornmentsOnEdit => true;
        protected override int RefreshDelayMilliseconds => HasPublishedTags ? 3200 : 800;
        protected override bool RefreshOnWorkspaceIdle => true;
        protected override int WorkspaceIdleThrottleMilliseconds => 4500;
        protected override int StructuralRefreshDelayMilliseconds => 900;

        protected override bool IsStructuralEdit(TextContentChangedEventArgs change)
        {
            // Multiple replacements and newlines are typical of WorkspaceEdit
            // (Extract Method / Inline / Make static / formatting).
            // Do not translate a stale CodeLens above an unrelated declaration.
            if (change == null || change.Changes.Count > 1) return true;
            foreach (ITextChange edit in change.Changes)
            {
                if (edit.NewText.IndexOf('\n') >= 0 || edit.OldText.IndexOf('\n') >= 0 ||
                    edit.NewText.IndexOf('\r') >= 0 || edit.OldText.IndexOf('\r') >= 0 ||
                    edit.NewText.Length > 16 || edit.OldText.Length > 16)
                    return true;
            }

            return ChangeTouchesPublishedTagLine(change);
        }
        protected override bool Allowed => base.Allowed && FeatureEnabled("disable-codelens.flag");

        protected override async Task<List<ITagSpan<InterLineAdornmentTag>>> FetchAsync(
            string uri, ITextSnapshot snapshot, int start, int end, CancellationToken token)
        {
            var result = new List<ITagSpan<InterLineAdornmentTag>>();
            if (snapshot.LineCount > 10000) return result;
            JToken response = await JavaLspRpc.RequestAsync(MethodName,
                new { textDocument = new { uri } }, token);
            // A null CodeLens response is allowed by LSP. The interface
            // fallback must still execute when native lenses are unavailable.
            var items = response as JArray ?? new JArray();

            var lines = new Dictionary<int, List<Link>>();
            int resolveCount = 0;
            // Bound the number of server-side resolves. Already-resolved
            // native CodeLens entries do not consume this budget.
            foreach (var item in items.OfType<JObject>().Take(120))
            {
                token.ThrowIfCancellationRequested();
                int line = (int?)item.SelectToken("range.start.line") ?? -1;
                int col = (int?)item.SelectToken("range.start.character") ?? 0;
                if (line < 0 || line >= snapshot.LineCount) continue;

                JObject command = item["command"] as JObject;
                if (command == null && resolveCount < 32)
                {
                    resolveCount++;
                    // JDT LS often resolves CodeLens commands lazily.
                    var resolved = await JavaLspRpc.RequestAsync("codeLens/resolve",
                        item.DeepClone(), token) as JObject;
                    command = resolved == null ? null : resolved["command"] as JObject;
                }
                if (command == null) continue;
                string title = ItalianCodeLensTitle((string)command["title"]);
                if (string.IsNullOrWhiteSpace(title)) continue;
                var row = snapshot.GetLineFromLineNumber(line);
                int safeOffset = row.Start.Position + Math.Max(0, Math.Min(col, row.Length));
                List<Link> entries;
                if (!lines.TryGetValue(line, out entries))
                    lines[line] = entries = new List<Link>();
                entries.Add(new Link { Title = title,
                    Kind = ActionKind(title, (string)command["command"]),
                    Offset = safeOffset, Uri = uri, Snapshot = snapshot });
            }

            // Native JDT CodeLens is authoritative. Add only missing
            // semantic references for fields and interface headings.
            // Retry a temporarily empty server response (at most twice).
            bool incomplete = await AddMissingInterfaceLensesAsync(
                uri, snapshot, lines, token);
            if (incomplete || items.Count == 0) RequestBoundedRetry(snapshot);

            foreach (var group in lines.OrderBy(x => x.Key))
            {
                // Native and fallback entries can report the same category
                // at the same declaration; show each action only once.
                var links = group.Value
                    .GroupBy(x => x.Field && x.Kind == "references"
                        ? "field:" + x.Offset.ToString()
                        : x.Kind ?? "title:" + x.Title,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.First()).Take(4).ToList();
                if (links.Count == 0) continue;
                var row = snapshot.GetLineFromLineNumber(group.Key);
                // Align CodeLens with the declaration's first non-whitespace character.
                string lineText = row.GetText();
                int indent = lineText.Length - lineText.TrimStart(' ', '\t').Length;
                int offset = row.Start.Position + indent;
                var span = new SnapshotSpan(snapshot, offset, 0);

                // The factory creates a NEW WPF tree for each render; never reuse UIElements.
                // isAboveLine=true, height=20: text itself remains on its own line.
                var capture = links;
                var tag = new InterLineAdornmentTag(
                    (adornment, textView, position) => MakeLens(capture),
                    true, 20, HorizontalPositioningMode.TextRelative, 0, null);
                result.Add(new TagSpan<InterLineAdornmentTag>(span, tag));
            }
            return result;
        }

        // DocumentSymbol can contain nested Java interfaces inside classes.
        // Iterate its hierarchy with a hard cap; never infer symbols from
        // text/regex and never issue unbounded semantic searches.
        private static IEnumerable<JObject> NestedSymbols(JArray symbols)
        {
            var pending = new Stack<JObject>(symbols.OfType<JObject>().Reverse());
            int visited = 0;
            while (pending.Count > 0 && ++visited <= 128)
            {
                JObject symbol = pending.Pop();
                yield return symbol;
                var children = symbol["children"] as JArray;
                if (children != null)
                    foreach (JObject child in children.OfType<JObject>().Reverse())
                        pending.Push(child);
            }
        }

        // Pack E1 fields/interfaces hotfix: query only semantic DocumentSymbol
        // identifiers (never guessed text positions). A bounded number of
        // requests complete missing field reference counts and interface lenses.
        private sealed class SemanticLensCandidate
        {
            internal int Line;
            internal int Column;
            internal int Offset;
            internal string Name;
            internal bool Field;
            internal bool NeedReferences;
            internal bool NeedImplementations;
        }

        private sealed class SemanticLensResult
        {
            internal SemanticLensCandidate Candidate;
            internal int? References;
            internal int? Implementations;
        }

        private static async Task<SemanticLensResult> QueryCandidateAsync(
            string uri, SemanticLensCandidate candidate, SemaphoreSlim limit,
            CancellationToken token)
        {
            await limit.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                var position = new { line = candidate.Line, character = candidate.Column };
                Task<JToken> references = candidate.NeedReferences
                    ? JavaLspRpc.RequestAsync("textDocument/references",
                        new { textDocument = new { uri }, position,
                              context = new { includeDeclaration = false } }, token)
                    : Task.FromResult<JToken>(null);
                Task<JToken> implementations = candidate.NeedImplementations
                    ? JavaLspRpc.RequestAsync("textDocument/implementation",
                        new { textDocument = new { uri }, position }, token)
                    : Task.FromResult<JToken>(null);
                await Task.WhenAll(references, implementations).ConfigureAwait(false);
                return new SemanticLensResult {
                    Candidate = candidate,
                    References = candidate.NeedReferences
                        ? CountLocations(references.Result) : null,
                    Implementations = candidate.NeedImplementations
                        ? CountLocations(implementations.Result) : null
                };
            }
            finally { limit.Release(); }
        }

        // True means the server returned a transient incomplete response.
        // The caller schedules up to two bounded retries without requiring the
        // user to close/reopen the interface document.
        private static async Task<bool> AddMissingInterfaceLensesAsync(
            string uri, ITextSnapshot snapshot, Dictionary<int, List<Link>> lines,
            CancellationToken token)
        {
            JArray symbols = await JavaLspRpc.RequestAsync("textDocument/documentSymbol",
                new { textDocument = new { uri } }, token) as JArray;
            if (symbols == null || symbols.Count == 0) return true;

            const int maxInterfaces = 12;
            const int maxFields = 32;
            int seenInterfaces = 0, seenFields = 0;
            // Distinguish two fields declared on one line: one native
            // reference lens cannot stand for both of them.
            var fieldsPerLine = new Dictionary<int, int>();
            foreach (JObject symbol in NestedSymbols(symbols))
            {
                int symbolKind = (int?)symbol["kind"] ?? -1;
                if (symbolKind != 8 && symbolKind != 22) continue;
                JToken at = symbol.SelectToken("selectionRange.start") ??
                    symbol.SelectToken("location.range.start") ??
                    symbol.SelectToken("range.start");
                int lineNumber = (int?)at?["line"] ?? -1;
                if (lineNumber < 0 || lineNumber >= snapshot.LineCount) continue;
                int previous;
                fieldsPerLine.TryGetValue(lineNumber, out previous);
                fieldsPerLine[lineNumber] = previous + 1;
            }
            var candidates = new List<SemanticLensCandidate>();
            foreach (JObject symbol in NestedSymbols(symbols))
            {
                token.ThrowIfCancellationRequested();
                int kind = (int?)symbol["kind"] ?? -1;
                bool isInterface = kind == 11; // LSP SymbolKind.Interface
                bool isField = kind == 8 || kind == 22; // Field / EnumMember
                if (!isInterface && !isField) continue;
                if (isInterface && ++seenInterfaces > maxInterfaces) continue;
                if (isField && ++seenFields > maxFields) continue;

                JToken position = symbol.SelectToken("selectionRange.start") ??
                    symbol.SelectToken("location.range.start") ??
                    symbol.SelectToken("range.start");
                if (position == null) continue;
                int line = (int?)position["line"] ?? -1;
                int column = (int?)position["character"] ?? -1;
                if (line < 0 || line >= snapshot.LineCount) continue;
                ITextSnapshotLine row = snapshot.GetLineFromLineNumber(line);
                if (column < 0 || column > row.Length) continue;
                int offset = row.Start.Position + column;

                List<Link> existing;
                if (!lines.TryGetValue(line, out existing))
                    lines[line] = existing = new List<Link>();

                int fieldsOnThisLine;
                fieldsPerLine.TryGetValue(line, out fieldsOnThisLine);
                bool nativeReferences = isInterface
                    ? existing.Any(x => x.Kind == "references" && !x.Field)
                    : existing.Any(x => x.Kind == "references" &&
                        (x.Field ? x.Offset == offset : fieldsOnThisLine == 1));
                bool nativeImplementations = isInterface &&
                    existing.Any(x => x.Kind == "implementations");
                if (isInterface || (isField && fieldsOnThisLine == 1))
                {
                    // Native ranges may point at a keyword/field modifier,
                    // not the identifier. Rebase only the matching heading.
                    foreach (Link link in existing)
                        if (!link.Field && (link.Kind == "references" ||
                            (isInterface && link.Kind == "implementations")))
                            link.Offset = offset;
                }
                var candidate = new SemanticLensCandidate {
                    Line = line, Column = column, Offset = offset,
                    Field = isField, Name = (string)symbol["name"] ?? "campo",
                    NeedReferences = !nativeReferences,
                    NeedImplementations = isInterface && !nativeImplementations
                };
                if (candidate.NeedReferences || candidate.NeedImplementations)
                    candidates.Add(candidate);
            }

            bool incomplete = false;
            // Limit concurrent JDT LS work to avoid flooding large Java classes.
            using (var limit = new SemaphoreSlim(4, 4))
            {
                var tasks = candidates.Select(x => QueryCandidateAsync(uri, x, limit, token))
                    .ToArray();
                SemanticLensResult[] results = await Task.WhenAll(tasks);
                token.ThrowIfCancellationRequested();
                foreach (SemanticLensResult result in results)
                {
                    var candidate = result.Candidate;
                    List<Link> existing = lines[candidate.Line];
                    if (candidate.NeedReferences)
                    {
                        if (!result.References.HasValue) incomplete = true;
                        else
                        {
                            if (result.References.Value == 0 && JavaWorkspaceActivity.IsBusy)
                                incomplete = true;
                            existing.Add(new Link {
                                Title = 
                                    result.References.Value + (result.References.Value == 1
                                    ? " riferimento" : " riferimenti"),
                                Kind = "references", Field = candidate.Field,
                                Offset = candidate.Offset, Uri = uri, Snapshot = snapshot });
                        }
                    }
                    if (candidate.NeedImplementations)
                    {
                        if (!result.Implementations.HasValue) incomplete = true;
                        else
                        {
                            if (result.Implementations.Value == 0 && JavaWorkspaceActivity.IsBusy)
                                incomplete = true;
                            existing.Add(new Link {
                                Title = result.Implementations.Value +
                                    (result.Implementations.Value == 1
                                    ? " implementazione" : " implementazioni"),
                                Kind = "implementations", Offset = candidate.Offset,
                                Uri = uri, Snapshot = snapshot });
                        }
                    }
                }
            }
            return incomplete;
        }

        private static int? CountLocations(JToken response)
        {
            if (response == null || response.Type == JTokenType.Null) return null;
            JArray array = response as JArray;
            if (array == null)
            {
                // LSP also permits a single Location/LocationLink.
                // Do not interpret arbitrary JSON objects as a match.
                var single = response as JObject;
                if (single == null ||
                    (single["uri"] == null && single["targetUri"] == null))
                    return null;
                array = new JArray(single.DeepClone());
            }
            // Location and LocationLink can both appear. Ignore duplicate
            // locations, for example when the server reports one target twice.
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JObject location in array.OfType<JObject>())
            {
                string uri = (string)location["uri"] ?? (string)location["targetUri"];
                JToken start = location.SelectToken("range.start") ??
                    location.SelectToken("targetSelectionRange.start") ??
                    location.SelectToken("targetRange.start");
                if (string.IsNullOrEmpty(uri) || start == null) continue;
                int line = (int?)start["line"] ?? -1;
                int col = (int?)start["character"] ?? -1;
                if (line >= 0 && col >= 0)
                    unique.Add(uri + "|" + line + "|" + col);
            }
            return unique.Count;
        }

        // JDT LS returns some native CodeLens labels in English even when
        // Visual Studio uses Italian. Only recognized count labels change;
        // commands, navigation, positions and numerical counts are unchanged.
        private static string ItalianCodeLensTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return title;
            // Handles "1 reference", "0 implementations", "field: 2 references".
            Match match = Regex.Match(title,
                @"^(?<prefix>.*?)(?<count>[0-9]+)\s+(?<kind>references?|implementations?)(?<suffix>\s*)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) return title;
            string count = match.Groups["count"].Value;
            bool singular = count.TrimStart('0') == "1";
            string kind = match.Groups["kind"].Value.StartsWith(
                "reference", StringComparison.OrdinalIgnoreCase)
                ? (singular ? "riferimento" : "riferimenti")
                : (singular ? "implementazione" : "implementazioni");
            return match.Groups["prefix"].Value + count + " " + kind +
                match.Groups["suffix"].Value;
        }

        private static string ActionKind(string title, string command)
        {
            string value = (title + " " + (command ?? "")).ToLowerInvariant();
            if (value.Contains("reference") || value.Contains("riferiment")) return "references";
            if (value.Contains("implementation") || value.Contains("implementazion")) return "implementations";
            return null;
        }

        private Border MakeLens(IList<Link> links)
        {
            var text = new TextBlock { FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis };
            text.SetResourceReference(TextBlock.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            var blue = new SolidColorBrush(Color.FromRgb(0, 122, 204));
            for (int i = 0; i < links.Count; i++)
            {
                if (i > 0) text.Inlines.Add(new Run("   ·   ") { Foreground = blue });
                var link = links[i];
                if (link.Kind == null)
                    // An unknown JDT LS command is informational, not a
                    // fake clickable link that silently does nothing.
                    text.Inlines.Add(new Run(link.Title));
                else
                {
                    var hyperlink = new Hyperlink(new Run(link.Title)) {
                        Foreground = blue, TextDecorations = TextDecorations.Underline,
                        ToolTip = link.Kind == "references"
                            ? "Java: Trova tutti i riferimenti"
                            : "Java: Vai alle implementazioni" };
                    hyperlink.Click += (sender, args) => {
                        args.Handled = true;
                        Navigate(link);
                    };
                    text.Inlines.Add(hyperlink);
                }
            }
            return new Border { Child = text,
                Height = 19, Padding = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = true };
        }

        private void Navigate(Link link)
        {
            if (View.IsClosed) return;
            ITextSnapshot currentSnapshot = View.TextBuffer.CurrentSnapshot;
            int currentOffset = link.Offset;
            if (!ReferenceEquals(link.Snapshot, currentSnapshot))
            {
                // CodeLens stays visible across source edits. Rebase the
                // original symbol position when its link is clicked.
                try
                {
                    currentOffset = new SnapshotPoint(link.Snapshot, link.Offset)
                        .TranslateTo(currentSnapshot, PointTrackingMode.Positive).Position;
                }
                catch (ArgumentException) { return; }
            }
            if (currentOffset < 0 || currentOffset >= currentSnapshot.Length) return;
            ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                if (link.Kind == "references")
                    await JavaReferenceSearch.RunAsync(link.Uri, currentSnapshot, currentOffset,
                        message => System.Diagnostics.Trace.WriteLine("Java CodeLens: " + message),
                        CancellationToken.None);
                else if (link.Kind == "implementations")
                    await JavaNavigation.ExecuteAsync(JavaEditorOperation.GoToImplementation,
                        link.Uri, currentSnapshot, currentOffset,
                        message => System.Diagnostics.Trace.WriteLine("Java CodeLens: " + message),
                        CancellationToken.None);
            }).FileAndForget("JavaLanguageSupport/CodeLensNavigate");
        }
    }
}
