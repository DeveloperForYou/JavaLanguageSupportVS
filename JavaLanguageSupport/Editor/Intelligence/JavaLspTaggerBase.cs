using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Task = System.Threading.Tasks.Task;

namespace JavaLanguageSupport.Editor.Intelligence
{
    /// <summary>
    /// Debounced per-view LSP tagger. Work runs asynchronously and never edits Java buffers.
    /// All WPF elements and TagsChanged notifications are created on the view dispatcher.
    /// </summary>
    internal abstract class JavaLspTaggerBase<TTag> : ITagger<TTag>, IDisposable where TTag : ITag
    {
        protected readonly IWpfTextView View;
        private readonly ITextDocumentFactoryService documents;
        private readonly DispatcherTimer timer;
        private CancellationTokenSource active;
        private List<ITagSpan<TTag>> current = new List<ITagSpan<TTag>>();
        private ITextSnapshot taggedSnapshot;
        private bool disposed;
        private int requestGeneration;
        private int lastStart = -1, lastEnd = -1, lastVersion = -1;

        protected JavaLspTaggerBase(IWpfTextView view, ITextDocumentFactoryService documents)
        {
            View = view;
            this.documents = documents;
            taggedSnapshot = view.TextBuffer.CurrentSnapshot;
            timer = new DispatcherTimer(DispatcherPriority.Background, view.VisualElement.Dispatcher);
            timer.Interval = TimeSpan.FromMilliseconds(500);
            timer.Tick += OnTick;
            view.TextBuffer.Changed += OnChanged;
            view.LayoutChanged += OnLayout;
            view.Closed += OnClosed;
            JavaWorkspaceActivity.BecameIdle += OnIndexIdle;
            JavaSemanticLegend.Changed += OnLegendChanged;
            Schedule();
        }

        protected abstract string MethodName { get; }
        protected abstract Task<List<ITagSpan<TTag>>> FetchAsync(
            string uri, ITextSnapshot snapshot, int start, int end, CancellationToken token);

        protected virtual bool Allowed => JavaLspRpc.Connected;
        protected virtual bool NeedsVisibleRange => false;

        // CodeLens needs slower refresh than colors/inlay hints: updating its
        // inter-line adornments on every keystroke moves the editor vertically.
        protected virtual int RefreshDelayMilliseconds => 500;
        protected virtual bool PreserveAdornmentsOnEdit => false;
        protected virtual bool RefreshOnWorkspaceIdle => true;
        protected bool HasPublishedTags => current.Count != 0;

        // Only classification taggers opt in. Inlay Hints and CodeLens must
        // continue discarding stale adornments immediately after an edit.
        protected virtual List<ITagSpan<TTag>> BuildImmediateTags(ITextSnapshot snapshot)
        {
            return null;
        }

        // Called only by providers that publish lexical tags (the Java semantic
        // tagger) after their derived fields have been initialized. This avoids
        // showing plain white identifiers when JDT LS is still preparing data.
        protected void PublishInitialTags()
        {
            if (disposed || View.IsClosed) return;
            try
            {
                var snapshot = View.TextBuffer.CurrentSnapshot;
                List<ITagSpan<TTag>> tags = BuildImmediateTags(snapshot);
                if (tags != null) Publish(snapshot, tags);
            }
            catch (Exception ex)
            {
                JavaLanguageStatus.LspFailure("Java editor/initialColors",
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        protected bool FeatureEnabled(string disableFlag)
        {
            string path = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "JavaLanguageSupportVS", disableFlag);
            return !File.Exists(path);
        }

        private void OnLegendChanged(object sender, EventArgs e) { InvalidateOnDispatcher(); }
        private void OnIndexIdle(object sender, EventArgs e)
        {
            if (RefreshOnWorkspaceIdle) InvalidateOnDispatcher();
        }
        private void OnLayout(object sender, TextViewLayoutChangedEventArgs e)
        {
            if (!NeedsVisibleRange) return;
            // Only repaint after the visible line range changes, not after every layout pass.
            int start, end;
            GetRange(View.TextBuffer.CurrentSnapshot, out start, out end);
            if (start != lastStart || end != lastEnd) Schedule();
        }

        private void OnChanged(object sender, TextContentChangedEventArgs e)
        {
            ScheduleOnDispatcher();
        }

        private void InvalidateOnDispatcher()
        {
            if (disposed) return;
            Action reset = () => { lastVersion = -1; Schedule(); };
            if (View.VisualElement.Dispatcher.CheckAccess()) reset();
            else View.VisualElement.Dispatcher.BeginInvoke(reset);
        }

        private void ScheduleOnDispatcher()
        {
            if (disposed) return;
            if (View.VisualElement.Dispatcher.CheckAccess()) Schedule();
            else View.VisualElement.Dispatcher.BeginInvoke(new Action(Schedule));
        }

        private void Schedule()
        {
            if (disposed || View.IsClosed) return;
            // Color classification must not disappear during the LSP debounce.
            // A semantic tagger can publish inexpensive local lexical tags at
            // once. Other taggers still clear obsolete UI adornments as before.
            ITextSnapshot nextSnapshot = View.TextBuffer.CurrentSnapshot;
            if (!ReferenceEquals(taggedSnapshot, nextSnapshot))
            {
                List<ITagSpan<TTag>> immediate = null;
                if (PreserveAdornmentsOnEdit && current.Count != 0)
                {
                    try
                    {
                        immediate = new List<ITagSpan<TTag>>(current.Count);
                        foreach (ITagSpan<TTag> old in current)
                        {
                            // Track the declaration's location across versions,
                            // preserving the same InterLineAdornmentTag and row.
                            SnapshotSpan translated = old.Span.TranslateTo(nextSnapshot,
                                SpanTrackingMode.EdgeExclusive);
                            immediate.Add(new TagSpan<TTag>(translated, old.Tag));
                        }
                    }
                    catch (ArgumentException) { immediate = null; }
                }
                if (immediate == null)
                {
                    try { immediate = BuildImmediateTags(nextSnapshot); }
                    catch (Exception ex)
                    {
                        JavaLanguageStatus.LspFailure("Java editor/immediateTags",
                            ex.GetType().Name + ": " + ex.Message);
                    }
                }
                Publish(nextSnapshot, immediate ?? new List<ITagSpan<TTag>>());
            }
            ++requestGeneration;
            if (active != null) active.Cancel();
            timer.Interval = TimeSpan.FromMilliseconds(RefreshDelayMilliseconds);
            timer.Stop();
            timer.Start();
        }

        private void GetRange(ITextSnapshot snapshot, out int start, out int end)
        {
            start = 0;
            end = snapshot.Length;
            if (!NeedsVisibleRange || View.TextViewLines == null || View.TextViewLines.Count == 0)
                return;
            var span = View.TextViewLines.FormattedSpan;
            if (span.Snapshot != snapshot) return;
            // Two lines of overscan to avoid cutting hints at the viewport boundary.
            int firstLine = Math.Max(0, span.Start.GetContainingLine().LineNumber - 2);
            int lastLine = Math.Min(snapshot.LineCount - 1, span.End.GetContainingLine().LineNumber + 2);
            start = snapshot.GetLineFromLineNumber(firstLine).Start.Position;
            end = snapshot.GetLineFromLineNumber(lastLine).End.Position;
        }

        private void OnTick(object sender, EventArgs args)
        {
            timer.Stop();
            // Fire-and-forget is safe here: ComputeAsync catches exceptions and checks disposal/generation.
            var ignored = ComputeAsync(requestGeneration);
        }

        private async Task ComputeAsync(int generation)
        {
            if (disposed || View.IsClosed) return;
            var snapshot = View.TextBuffer.CurrentSnapshot;
            int start, end;
            GetRange(snapshot, out start, out end);
            if (generation != requestGeneration) return;
            if (lastVersion == snapshot.Version.VersionNumber && lastStart == start && lastEnd == end)
                return;
            lastVersion = snapshot.Version.VersionNumber;
            lastStart = start;
            lastEnd = end;
            if (active != null) { active.Dispose(); active = null; }
            active = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var token = active.Token;
            var empty = new List<ITagSpan<TTag>>();
            if (!Allowed)
            {
                Publish(snapshot, empty);
                return;
            }
            try
            {
                ITextDocument document;
                if (documents == null || !documents.TryGetTextDocument(View.TextBuffer, out document))
                    return;
                string uri = JavaLspRpc.FileUri(document);
                if (string.IsNullOrWhiteSpace(uri)) return;
                // Captures the WPF synchronization context; never build WPF elements off-thread.
                var tags = await FetchAsync(uri, snapshot, start, end, token);
                if (!disposed && !View.IsClosed && !token.IsCancellationRequested &&
                    generation == requestGeneration &&
                    ReferenceEquals(snapshot, View.TextBuffer.CurrentSnapshot))
                    Publish(snapshot, tags ?? empty);
            }
            catch (OperationCanceledException) { /* Content changed or bounded request timed out. */ }
            catch (Exception ex)
            {
                JavaLanguageStatus.LspFailure(MethodName, ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void Publish(ITextSnapshot snapshot, List<ITagSpan<TTag>> tags)
        {
            if (disposed) return;
            taggedSnapshot = snapshot;
            current = tags;
            var handler = TagsChanged;
            if (handler != null)
                handler(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
        }

        public IEnumerable<ITagSpan<TTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (disposed || spans == null || spans.Count == 0 ||
                !ReferenceEquals(spans[0].Snapshot, taggedSnapshot))
                return Enumerable.Empty<ITagSpan<TTag>>();
            // Zero-length inline tags must be tested by point containment, not IntersectsWith.
            return current.Where(item => spans.Any(s =>
                item.Span.IsEmpty
                    ? item.Span.Start.Position >= s.Start.Position &&
                      item.Span.Start.Position <= s.End.Position
                    : item.Span.IntersectsWith(s))).ToArray();
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        private void OnClosed(object sender, EventArgs args) { Dispose(); }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            timer.Stop();
            timer.Tick -= OnTick;
            if (active != null) { active.Cancel(); active.Dispose(); active = null; }
            View.TextBuffer.Changed -= OnChanged;
            View.LayoutChanged -= OnLayout;
            View.Closed -= OnClosed;
            JavaWorkspaceActivity.BecameIdle -= OnIndexIdle;
            JavaSemanticLegend.Changed -= OnLegendChanged;
            current.Clear();
        }
    }
}
