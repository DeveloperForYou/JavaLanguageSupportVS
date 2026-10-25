using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
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
                string title = (string)command["title"];
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

            // JDT LS does not always return CodeLens commands on interface
            // declarations, even when implementationCodeLens is enabled.
            // Complete only the missing categories using semantic LSP queries.
            // The existing CodeLens (when present) remains authoritative.
            await AddMissingInterfaceLensesAsync(uri, snapshot, lines, token);

            foreach (var group in lines.OrderBy(x => x.Key))
            {
                // Native and fallback entries can report the same category
                // at the same declaration; show each action only once.
                var links = group.Value
                    .GroupBy(x => x.Kind ?? "title:" + x.Title,
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

        // When the native JDT server omits an interface heading,
        // use semantic LSP locations as a bounded fallback.
        private static async Task AddMissingInterfaceLensesAsync(
            string uri, ITextSnapshot snapshot, Dictionary<int, List<Link>> lines,
            CancellationToken token)
        {
            JArray symbols = await JavaLspRpc.RequestAsync("textDocument/documentSymbol",
                new { textDocument = new { uri } }, token) as JArray;
            if (symbols == null) return;

            int checkedInterfaces = 0;
            foreach (JObject symbol in NestedSymbols(symbols))
            {
                token.ThrowIfCancellationRequested();
                // LSP SymbolKind.Interface = 11. Method declarations are left to
                // native JDT CodeLens; the fallback targets interface headings.
                if ((int?)symbol["kind"] != 11) continue;
                if (++checkedInterfaces > 12) break;

                // DocumentSymbol.selectionRange denotes the identifier. A
                // SymbolInformation location is supported as a fallback.
                JToken position = symbol.SelectToken("selectionRange.start") ??
                    symbol.SelectToken("location.range.start") ??
                    symbol.SelectToken("range.start");
                if (position == null) continue;
                int line = (int?)position["line"] ?? -1;
                int column = (int?)position["character"] ?? -1;
                if (line < 0 || line >= snapshot.LineCount) continue;
                ITextSnapshotLine row = snapshot.GetLineFromLineNumber(line);
                if (column < 0 || column > row.Length) continue;

                List<Link> existing;
                if (!lines.TryGetValue(line, out existing))
                    lines[line] = existing = new List<Link>();
                // Use the authoritative interface identifier also for
                // native CodeLens. Its range.start may point at the
                // declaration keyword instead of the symbol itself.
                int offset = row.Start.Position + column;
                foreach (Link link in existing)
                    if (link.Kind == "references" ||
                        link.Kind == "implementations") link.Offset = offset;
                bool needReferences = !existing.Any(x => x.Kind == "references");
                bool needImplementations = !existing.Any(x => x.Kind == "implementations");
                if (!needReferences && !needImplementations) continue;
                var requestPosition = new { line, character = column };
                // Send independent searches together; only the LSP server does
                // symbol resolution, and this code never scans the filesystem.
                Task<JToken> referencesTask = needReferences
                    ? JavaLspRpc.RequestAsync("textDocument/references",
                        new { textDocument = new { uri }, position = requestPosition,
                              context = new { includeDeclaration = false } }, token)
                    : Task.FromResult<JToken>(null);
                Task<JToken> implementationsTask = needImplementations
                    ? JavaLspRpc.RequestAsync("textDocument/implementation",
                        new { textDocument = new { uri }, position = requestPosition }, token)
                    : Task.FromResult<JToken>(null);
                await Task.WhenAll(referencesTask, implementationsTask);
                if (needReferences)
                {
                    int? count = CountLocations(referencesTask.Result);
                    if (count.HasValue)
                        existing.Add(new Link {
                            Title = count.Value + (count.Value == 1 ? " riferimento" : " riferimenti"),
                            Kind = "references", Offset = offset,
                            Uri = uri, Snapshot = snapshot });
                }
                if (needImplementations)
                {
                    int? count = CountLocations(implementationsTask.Result);
                    if (count.HasValue)
                        existing.Add(new Link {
                            Title = count.Value + (count.Value == 1 ? " implementazione" : " implementazioni"),
                            Kind = "implementations", Offset = offset,
                            Uri = uri, Snapshot = snapshot });
                }
            }
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
