using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json.Linq;

namespace JavaLanguageSupport.Editor.Intelligence
{
    internal static class JavaDocumentHighlightNames
    {
        internal const string Read = "Java JDT LS - Semantic Read Highlight";
        internal const string Write = "Java JDT LS - Semantic Write Highlight";
    }

    [Export(typeof(EditorFormatDefinition))]
    [Name(JavaDocumentHighlightNames.Read)]
    [UserVisible(true)]
    internal sealed class JavaDocumentReadMarkerFormat : MarkerFormatDefinition
    {
        public JavaDocumentReadMarkerFormat()
        {
            DisplayName = "Java: occorrenze del simbolo (lettura)";
            BackgroundColor = Color.FromArgb(50, 75, 145, 230);
            ForegroundColor = Color.FromArgb(150, 75, 145, 230);
            ZOrder = 4;
        }
    }

    [Export(typeof(EditorFormatDefinition))]
    [Name(JavaDocumentHighlightNames.Write)]
    [UserVisible(true)]
    internal sealed class JavaDocumentWriteMarkerFormat : MarkerFormatDefinition
    {
        public JavaDocumentWriteMarkerFormat()
        {
            DisplayName = "Java: occorrenze del simbolo (scrittura)";
            BackgroundColor = Color.FromArgb(65, 237, 170, 69);
            ForegroundColor = Color.FromArgb(160, 237, 170, 69);
            ZOrder = 5;
        }
    }

    [Export(typeof(IViewTaggerProvider))]
    [Name("Java JDT LS Document Highlights")]
    [ContentType("java")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    [TagType(typeof(TextMarkerTag))]
    internal sealed class JavaDocumentHighlightTaggerProvider : IViewTaggerProvider
    {
        [Import] internal ITextDocumentFactoryService Documents = null;

        public ITagger<T> CreateTagger<T>(ITextView view, ITextBuffer buffer) where T : ITag
        {
            var wpf = view as IWpfTextView;
            if (wpf == null || !ReferenceEquals(view.TextBuffer, buffer)) return null;
            return wpf.Properties.GetOrCreateSingletonProperty(() =>
                new JavaDocumentHighlightTagger(wpf, Documents)) as ITagger<T>;
        }
    }

    internal sealed class JavaDocumentHighlightTagger : ITagger<TextMarkerTag>, IDisposable
    {
        private readonly IWpfTextView view;
        private readonly ITextDocumentFactoryService documents;
        private readonly DispatcherTimer timer;
        private readonly List<ITagSpan<TextMarkerTag>> empty = new List<ITagSpan<TextMarkerTag>>();
        private List<ITagSpan<TextMarkerTag>> current;
        private ITextSnapshot snapshot;
        private CancellationTokenSource active;
        private int generation;
        private bool disposed;

        internal JavaDocumentHighlightTagger(IWpfTextView view, ITextDocumentFactoryService documents)
        {
            this.view = view;
            this.documents = documents;
            snapshot = view.TextBuffer.CurrentSnapshot;
            current = empty;
            timer = new DispatcherTimer(DispatcherPriority.Background, view.VisualElement.Dispatcher);
            timer.Tick += OnTimer;
            view.Caret.PositionChanged += OnCaret;
            view.TextBuffer.Changed += OnTextChanged;
            view.Closed += OnClosed;
            JavaWorkspaceActivity.BecameIdle += OnIdle;
            Schedule(350);
        }

        private bool Enabled
        {
            get
            {
                string flag = Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                    "JavaLanguageSupportVS", "disable-document-highlights.flag");
                return !File.Exists(flag);
            }
        }

        private void OnCaret(object sender, CaretPositionChangedEventArgs args)
        {
            // The highlight query is made for a settled caret position, not every pixel
            // of selection/typing. No filesystem scans, no reference-index requests.
            Schedule(320);
        }

        private void OnTextChanged(object sender, TextContentChangedEventArgs args)
        {
            // A TextDocumentHighlight range belongs to one immutable snapshot.
            // Never translate old semantic occurrences after a refactoring.
            if (view.VisualElement.Dispatcher.CheckAccess())
            {
                Clear(view.TextBuffer.CurrentSnapshot);
                Schedule(1000);
            }
            else
            {
                view.VisualElement.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (disposed) return;
                    Clear(view.TextBuffer.CurrentSnapshot);
                    Schedule(1000);
                }));
            }
        }

        private void OnIdle(object sender, EventArgs args)
        {
            Action refresh = () => { if (!disposed && current.Count == 0) Schedule(650); };
            if (view.VisualElement.Dispatcher.CheckAccess()) refresh();
            else view.VisualElement.Dispatcher.BeginInvoke(refresh);
        }

        private void Schedule(int delayMilliseconds)
        {
            if (disposed || view.IsClosed) return;
            ++generation;
            if (active != null) active.Cancel();
            timer.Stop();
            timer.Interval = TimeSpan.FromMilliseconds(delayMilliseconds);
            timer.Start();
        }

        private void OnTimer(object sender, EventArgs args)
        {
            timer.Stop();
            var ignored = QueryAsync(generation);
        }

        private async Task QueryAsync(int requestedGeneration)
        {
            if (disposed || view.IsClosed) return;
            ITextSnapshot requestedSnapshot = view.TextBuffer.CurrentSnapshot;
            if (requestedGeneration != generation) return;
            if (!Enabled || !JavaLspRpc.Connected || documents == null)
            {
                Clear(requestedSnapshot);
                return;
            }
            ITextDocument document;
            if (!documents.TryGetTextDocument(view.TextBuffer, out document)) return;
            string uri = JavaLspRpc.FileUri(document);
            if (string.IsNullOrEmpty(uri)) return;
            int caret = Math.Min(view.Caret.Position.BufferPosition.Position, requestedSnapshot.Length);
            // Do not query on whitespace, punctuation, or an empty document.
            int identifier = caret;
            if (identifier > 0 && (identifier == requestedSnapshot.Length ||
                !IsIdentifier(requestedSnapshot[identifier])) &&
                IsIdentifier(requestedSnapshot[identifier - 1])) identifier--;
            if (identifier >= requestedSnapshot.Length || !IsIdentifier(requestedSnapshot[identifier]))
            {
                Clear(requestedSnapshot);
                return;
            }
            var source = new CancellationTokenSource(TimeSpan.FromSeconds(7));
            active = source;
            try
            {
                JToken reply = await JavaLspRpc.RequestAsync("textDocument/documentHighlight",
                    new { textDocument = new { uri },
                          position = JavaLspRpc.Position(requestedSnapshot, identifier) },
                    source.Token);
                if (disposed || source.IsCancellationRequested || requestedGeneration != generation ||
                    !ReferenceEquals(requestedSnapshot, view.TextBuffer.CurrentSnapshot)) return;
                var highlights = new List<ITagSpan<TextMarkerTag>>();
                var unique = new HashSet<string>();
                foreach (JObject item in (reply as JArray ?? new JArray()).OfType<JObject>().Take(800))
                {
                    int from, to;
                    if (!TryOffset(requestedSnapshot, item.SelectToken("range.start"), out from) ||
                        !TryOffset(requestedSnapshot, item.SelectToken("range.end"), out to) ||
                        to <= from || to > requestedSnapshot.Length) continue;
                    if (!unique.Add(from + ":" + to)) continue;
                    string format = (int?)item["kind"] == 3 ?
                        JavaDocumentHighlightNames.Write : JavaDocumentHighlightNames.Read;
                    highlights.Add(new TagSpan<TextMarkerTag>(
                        new SnapshotSpan(requestedSnapshot, from, to - from),
                        new TextMarkerTag(format)));
                }
                Publish(requestedSnapshot, highlights);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                JavaLanguageStatus.LspFailure("Java/documentHighlight", ex.GetType().Name);
            }
            finally
            {
                if (ReferenceEquals(active, source)) active = null;
                source.Dispose();
            }
        }

        private static bool IsIdentifier(char value)
        {
            return char.IsLetterOrDigit(value) || value == '_' || value == '$';
        }

        private static bool TryOffset(ITextSnapshot snapshot, JToken position, out int offset)
        {
            offset = -1;
            if (position == null) return false;
            int row = (int?)position["line"] ?? -1;
            int col = (int?)position["character"] ?? -1;
            if (row < 0 || row >= snapshot.LineCount) return false;
            var line = snapshot.GetLineFromLineNumber(row);
            if (col < 0 || col > line.Length) return false;
            offset = line.Start.Position + col;
            return true;
        }

        private void Clear(ITextSnapshot next)
        {
            if (disposed) return;
            if (!ReferenceEquals(snapshot, next) || current.Count != 0)
                Publish(next, empty);
        }

        private void Publish(ITextSnapshot next, List<ITagSpan<TextMarkerTag>> tags)
        {
            if (disposed) return;
            snapshot = next;
            current = tags;
            var handler = TagsChanged;
            if (handler != null)
                handler(this, new SnapshotSpanEventArgs(new SnapshotSpan(next, 0, next.Length)));
        }

        public IEnumerable<ITagSpan<TextMarkerTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (disposed || spans == null || spans.Count == 0 ||
                !ReferenceEquals(spans[0].Snapshot, snapshot))
                return Enumerable.Empty<ITagSpan<TextMarkerTag>>();
            return current.Where(t => spans.Any(s => t.Span.IntersectsWith(s))).ToArray();
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        private void OnClosed(object sender, EventArgs args) { Dispose(); }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            timer.Stop();
            timer.Tick -= OnTimer;
            if (active != null) active.Cancel();
            view.Caret.PositionChanged -= OnCaret;
            view.TextBuffer.Changed -= OnTextChanged;
            view.Closed -= OnClosed;
            JavaWorkspaceActivity.BecameIdle -= OnIdle;
            current = empty;
        }
    }
}
