using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using JavaLanguageSupport.Editor.Refactoring;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;

namespace JavaLanguageSupport.Editor.Navigation
{
    /// <summary>
    /// Ctrl + click e memorizzazione del token sotto il click destro.
    /// Il menu vero e proprio è aggiunto a IDM_VS_CTXT_CODEWIN tramite VSCT:
    /// NON si sostituisce il menu contestuale nativo di Visual Studio.
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("java")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class JavaCtrlClickNavigation : IWpfTextViewCreationListener
    {
        [Import] internal ITextDocumentFactoryService DocumentFactory = null;

        private static WeakReference<IWpfTextView> contextView;
        private static int contextOffset = -1;
        private static DateTime contextTimestampUtc;

        public void TextViewCreated(IWpfTextView view)
        {
            if (view == null || !view.TextBuffer.ContentType.IsOfType("java")) return;
            if (view.Properties.ContainsProperty(typeof(JavaMouseHandler))) return;
            var handler = new JavaMouseHandler(view, DocumentFactory);
            view.Properties.AddProperty(typeof(JavaMouseHandler), handler);
        }

        private static void RememberContext(IWpfTextView view, int position)
        {
            contextView = new WeakReference<IWpfTextView>(view);
            contextOffset = position;
            contextTimestampUtc = DateTime.UtcNow;
        }

        private static void ClearContext()
        {
            contextView = null;
            contextOffset = -1;
        }

        internal static bool HasRecentContextSymbol
        {
            get
            {
                IWpfTextView view;
                return contextView != null &&
                    contextView.TryGetTarget(out view) && view != null && !view.IsClosed &&
                    (DateTime.UtcNow - contextTimestampUtc) < TimeSpan.FromSeconds(8) &&
                    contextOffset >= 0 && contextOffset < view.TextBuffer.CurrentSnapshot.Length &&
                    view.TextBuffer.ContentType.IsOfType("java") &&
                    IsIdentifier(view.TextBuffer.CurrentSnapshot[contextOffset]);
            }
        }

        /// <summary>
        /// Prima del comando contestuale porta il caret sul token cliccato, senza
        /// alterare la selezione quando il menu viene semplicemente aperto.
        /// </summary>
        internal static void PrepareContextCaret(IVsTextManager textManager, IComponentModel model)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!HasRecentContextSymbol || textManager == null || model == null) return;
            IWpfTextView clicked;
            if (!contextView.TryGetTarget(out clicked)) return;

            var adapters = model.GetService<IVsEditorAdaptersFactoryService>();
            if (adapters == null) return;
            IVsTextView vsView;
            if (textManager.GetActiveView(1, null, out vsView) < 0 || vsView == null) return;
            var active = adapters.GetWpfTextView(vsView);
            if (!ReferenceEquals(active, clicked)) return;

            var snapshot = clicked.TextBuffer.CurrentSnapshot;
            if (contextOffset >= 0 && contextOffset < snapshot.Length)
                clicked.Caret.MoveTo(new SnapshotPoint(snapshot, contextOffset));
            // A context position is one-shot. Future keyboard/navigation commands
            // must use the CURRENT caret, not a previous right click.
            ClearContext();
        }

        private static bool IsIdentifier(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == '$';
        }

        private sealed class JavaMouseHandler
        {
            private readonly IWpfTextView view;
            private readonly ITextDocumentFactoryService documents;
            private Cursor previousCursor;
            private bool changedCursor;

            internal JavaMouseHandler(IWpfTextView view, ITextDocumentFactoryService documents)
            {
                this.view = view;
                this.documents = documents;
                view.VisualElement.PreviewMouseRightButtonDown += OnRightButtonDown;
                view.VisualElement.PreviewMouseLeftButtonUp += OnLeftButtonUp;
                view.VisualElement.MouseMove += OnMouseMove;
                view.VisualElement.MouseLeave += OnMouseLeave;
                view.Closed += OnClosed;
            }

            private SnapshotPoint? PointAt(MouseEventArgs args)
            {
                if (view.IsClosed || view.TextViewLines == null) return null;
                Point p = args.GetPosition(view.VisualElement);
                if (p.X < 0 || p.Y < 0 || p.X > view.VisualElement.ActualWidth ||
                    p.Y > view.VisualElement.ActualHeight) return null;
                // Le coordinate di ITextViewLine sono coordinate di testo, non WPF.
                var line = view.TextViewLines.GetTextViewLineContainingYCoordinate(p.Y + view.ViewportTop);
                if (line == null) return null;
                var candidate = line.GetBufferPositionFromXCoordinate(p.X + view.ViewportLeft, true);
                if (!candidate.HasValue || candidate.Value.Snapshot.TextBuffer != view.TextBuffer)
                    return null;
                return candidate;
            }

            private SnapshotPoint? IdentifierAt(MouseEventArgs args)
            {
                var p = PointAt(args);
                if (!p.HasValue) return null;
                var snapshot = p.Value.Snapshot;
                int offset = p.Value.Position;
                if (offset < 0 || offset >= snapshot.Length || !IsIdentifier(snapshot[offset]))
                    return null;
                return p;
            }

            private void OnRightButtonDown(object sender, MouseButtonEventArgs args)
            {
                var p = IdentifierAt(args);
                if (p.HasValue) RememberContext(view, p.Value.Position);
                else ClearContext();
                // Non intercettiamo il tasto destro: menu VS resta quello originale.
            }

            private async void OnLeftButtonUp(object sender, MouseButtonEventArgs args)
            {
                if (args.ChangedButton != MouseButton.Left ||
                    (Keyboard.Modifiers & ModifierKeys.Control) == 0 ||
                    (Keyboard.Modifiers & (ModifierKeys.Alt | ModifierKeys.Shift)) != 0 ||
                    !JavaLspRpc.Connected) return;

                var clicked = IdentifierAt(args);
                if (!clicked.HasValue || documents == null) return;
                ITextDocument document;
                if (!documents.TryGetTextDocument(view.TextBuffer, out document)) return;
                string uri = JavaLspRpc.FileUri(document);
                if (string.IsNullOrEmpty(uri)) return;
                args.Handled = true;
                try
                {
                    // Usa la stessa risoluzione file:// + jdt:// del comando VSIX esistente.
                    await JavaNavigation.ExecuteAsync(JavaEditorOperation.GoToDefinition,
                        uri, clicked.Value.Snapshot, clicked.Value.Position,
                        message => System.Diagnostics.Trace.WriteLine("Java Ctrl+Click: " + message),
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine("Java Ctrl+Click: " + ex);
                }
            }

            private void OnMouseMove(object sender, MouseEventArgs args)
            {
                bool hand = (Keyboard.Modifiers & ModifierKeys.Control) != 0 &&
                    IdentifierAt(args).HasValue;
                if (hand && !changedCursor)
                {
                    previousCursor = view.VisualElement.Cursor;
                    view.VisualElement.Cursor = Cursors.Hand;
                    changedCursor = true;
                }
                else if (!hand) RestoreCursor();
            }

            private void OnMouseLeave(object sender, MouseEventArgs args)
            {
                RestoreCursor();
            }

            private void RestoreCursor()
            {
                if (!changedCursor) return;
                view.VisualElement.Cursor = previousCursor;
                changedCursor = false;
            }

            private void OnClosed(object sender, EventArgs args)
            {
                RestoreCursor();
                view.VisualElement.PreviewMouseRightButtonDown -= OnRightButtonDown;
                view.VisualElement.PreviewMouseLeftButtonUp -= OnLeftButtonUp;
                view.VisualElement.MouseMove -= OnMouseMove;
                view.VisualElement.MouseLeave -= OnMouseLeave;
                view.Closed -= OnClosed;
            }
        }
    }
}
