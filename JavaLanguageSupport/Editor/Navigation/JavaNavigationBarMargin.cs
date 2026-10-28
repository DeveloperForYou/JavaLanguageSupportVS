using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json.Linq;

namespace JavaLanguageSupport.Editor.Navigation
{
    /// <summary>
    /// Java-only editor margin. This is an embedded navigation bar, not the
    /// Visual Studio native IVsDropdownBar implementation or a tool window.
    /// All symbol ranges come from JDT LS documentSymbol (no regex parser).
    /// </summary>
    [Export(typeof(IWpfTextViewMarginProvider))]
    [Name(JavaNavigationBarMargin.MarginName)]
    [MarginContainer(PredefinedMarginNames.Top)]
    [ContentType("java")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class JavaNavigationBarMarginProvider : IWpfTextViewMarginProvider
    {
        [Import] internal ITextDocumentFactoryService Documents = null;

        public IWpfTextViewMargin CreateMargin(IWpfTextViewHost host,
            IWpfTextViewMargin marginContainer)
        {
            if (host == null || Documents == null || host.TextView == null ||
                !host.TextView.TextBuffer.ContentType.IsOfType("java")) return null;
            return new JavaNavigationBarMargin(host.TextView, Documents);
        }
    }

    internal sealed class JavaNavigationBarMargin : Border, IWpfTextViewMargin
    {
        internal const string MarginName = "Java JDT LS Navigation Bar";

        private sealed class Symbol
        {
            internal string Name;
            internal string Caption;
            internal int Kind;
            internal int Start;
            internal int End;
            internal int Target;
            internal Symbol Owner;
            public override string ToString() { return Caption; }
        }

        private readonly IWpfTextView view;
        private readonly ITextDocumentFactoryService documents;
        private readonly ComboBox types;
        private readonly ComboBox members;
        private readonly TextBlock state;
        private readonly DispatcherTimer timer;
        private readonly List<Symbol> typeSymbols = new List<Symbol>();
        private readonly List<Symbol> memberSymbols = new List<Symbol>();
        private readonly List<Symbol> noSymbols = new List<Symbol>();
        private ITextSnapshot symbolSnapshot;
        private Symbol shownType;
        private CancellationTokenSource active;
        private bool settingSelection;
        private bool disposed;
        private int generation;
        private int pendingSymbolRetries;

        internal JavaNavigationBarMargin(IWpfTextView view, ITextDocumentFactoryService documents)
        {
            this.view = view;
            this.documents = documents;
            Height = 29;
            Padding = new Thickness(4, 1, 4, 1);
            SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            SetResourceReference(BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            BorderThickness = new Thickness(0, 0, 0, 1);

            types = MakeCombo("Tipi Java", 255);
            members = MakeCombo("Metodi e membri Java", 330);
            members.IsEnabled = false;
            state = new TextBlock { Text = "Connessione a JDT LS…", VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            state.SetResourceReference(TextBlock.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            var refresh = MakeRefreshButton();
            var row = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(types, Dock.Left);
            DockPanel.SetDock(members, Dock.Left);
            DockPanel.SetDock(refresh, Dock.Left);
            row.Children.Add(types);
            row.Children.Add(members);
            row.Children.Add(refresh);
            row.Children.Add(state);
            Child = row;

            timer = new DispatcherTimer(DispatcherPriority.Background, view.VisualElement.Dispatcher);
            timer.Tick += OnTimer;
            types.SelectionChanged += OnTypeSelected;
            members.SelectionChanged += OnMemberSelected;
            refresh.Click += (sender, args) => Schedule(40);
            view.Caret.PositionChanged += OnCaret;
            view.TextBuffer.Changed += OnChanged;
            view.Closed += OnClosed;
            JavaWorkspaceActivity.BecameIdle += OnIdle;
            Schedule(700);
        }

        // The stock WPF Button template uses a pale-blue hover/pressed brush.
        // In Visual Studio's dark theme it retains the light navigation glyph,
        // making the refresh command nearly invisible. Keep the icon and chrome
        // themed together; the WPF overlay reads dynamic VS brushes, so it also
        // adapts when the IDE theme changes without recreating the margin.
        private static Button MakeRefreshButton()
        {
            var refresh = new Button { Content = "↻", MinWidth = 28, Width = 28, Height = 23,
                Margin = new Thickness(5, 0, 0, 0), Padding = new Thickness(0),
                ToolTip = "Aggiorna i simboli Java" };
            refresh.SetResourceReference(Control.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            refresh.SetResourceReference(Control.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            refresh.SetResourceReference(Control.BorderBrushProperty,
                EnvironmentColors.ToolWindowBorderBrushKey);

            var template = new ControlTemplate(typeof(Button));
            var layers = new FrameworkElementFactory(typeof(Grid));

            var chrome = new FrameworkElementFactory(typeof(Border));
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
            chrome.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            chrome.SetBinding(Border.BackgroundProperty,
                new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
            chrome.SetBinding(Border.BorderBrushProperty,
                new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
            layers.AppendChild(chrome);

            // A VS-themed border-color overlay is legible both on the dark
            // and the light palette; unlike the system Button template it never
            // switches the background to a light blue with a white glyph.
            var hover = new FrameworkElementFactory(typeof(Border));
            hover.Name = "JavaNavigationRefreshHover";
            hover.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
            hover.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            hover.SetValue(UIElement.IsHitTestVisibleProperty, false);
            hover.SetValue(UIElement.OpacityProperty, 0.75);
            hover.SetResourceReference(Border.BackgroundProperty,
                EnvironmentColors.ToolWindowBorderBrushKey);
            layers.AppendChild(hover);

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty,
                HorizontalAlignment.Center);
            content.SetValue(FrameworkElement.VerticalAlignmentProperty,
                VerticalAlignment.Center);
            content.SetValue(UIElement.IsHitTestVisibleProperty, false);
            content.SetBinding(TextElement.ForegroundProperty,
                new Binding("Foreground") { RelativeSource = RelativeSource.TemplatedParent });
            layers.AppendChild(content);
            template.VisualTree = layers;

            var mouseOver = new Trigger {
                Property = UIElement.IsMouseOverProperty, Value = true };
            mouseOver.Setters.Add(new Setter(UIElement.VisibilityProperty,
                Visibility.Visible, "JavaNavigationRefreshHover"));
            template.Triggers.Add(mouseOver);

            var keyboardFocus = new Trigger {
                Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
            keyboardFocus.Setters.Add(new Setter(UIElement.VisibilityProperty,
                Visibility.Visible, "JavaNavigationRefreshHover"));
            template.Triggers.Add(keyboardFocus);

            var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(UIElement.OpacityProperty,
                1.0, "JavaNavigationRefreshHover"));
            template.Triggers.Add(pressed);

            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
            template.Triggers.Add(disabled);

            refresh.Template = template;
            return refresh;
        }

        private static ComboBox MakeCombo(string tip, double width)
        {
            var box = new ComboBox { Width = width, MinHeight = 23,
                Height = 24, Margin = new Thickness(2, 0, 2, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = tip, IsTextSearchEnabled = true, IsEditable = false };
            // The default WPF ComboBox template paints a LIGHT popup in a DARK
            // Visual Studio theme; its inherited foreground stays light and the
            // list becomes unreadable. Use Visual Studio's actual themed styles
            // for the closed control AND for every dropdown item. The resource
            // references update automatically when the user changes VS theme.
            box.SetResourceReference(FrameworkElement.StyleProperty,
                VsResourceKeys.ComboBoxStyleKey);
            box.SetResourceReference(ComboBox.ItemContainerStyleProperty,
                VsResourceKeys.ComboBoxItemStyleKey);
            return box;
        }

        private void OnChanged(object sender, TextContentChangedEventArgs e)
        {
            Action refresh = () =>
            {
                if (disposed) return;
                // Disable stale navigation until the LSP catches up with the new snapshot.
                types.IsEnabled = false;
                members.IsEnabled = false;
                state.Text = "Aggiornamento simboli…";
                pendingSymbolRetries = 0;
                Schedule(1100);
            };
            if (view.VisualElement.Dispatcher.CheckAccess()) refresh();
            else view.VisualElement.Dispatcher.BeginInvoke(refresh);
        }

        private void OnCaret(object sender, CaretPositionChangedEventArgs e)
        {
            if (disposed) return;
            // Navigation bar selection follows the caret locally; no JDT request here.
            UpdateSelectionAtCaret();
        }

        private void OnIdle(object sender, EventArgs e)
        {
            Action refresh = () =>
            {
                if (disposed) return;
                if (typeSymbols.Count == 0 ||
                    !ReferenceEquals(symbolSnapshot, view.TextBuffer.CurrentSnapshot))
                {
                    // A completed Maven/Gradle import can arrive much later
                    // than the first documentSymbol response.
                    pendingSymbolRetries = 0;
                    Schedule(450);
                }
            };
            if (view.VisualElement.Dispatcher.CheckAccess()) refresh();
            else view.VisualElement.Dispatcher.BeginInvoke(refresh);
        }

        private void Schedule(int milliseconds)
        {
            if (disposed || view.IsClosed) return;
            ++generation;
            if (active != null) active.Cancel();
            timer.Stop();
            timer.Interval = TimeSpan.FromMilliseconds(milliseconds);
            timer.Start();
        }

        private void OnTimer(object sender, EventArgs e)
        {
            timer.Stop();
            var ignored = FetchSymbolsAsync(generation);
        }

        private async Task FetchSymbolsAsync(int version)
        {
            if (disposed || view.IsClosed || version != generation) return;
            if (!JavaLspRpc.Connected)
            {
                // JDT LS may start AFTER the margin is created. Keep waiting
                // automatically; don't require repeated manual refresh clicks.
                state.Text = "Connessione a JDT LS in corso…";
                Schedule(1800);
                return;
            }
            ITextSnapshot requested = view.TextBuffer.CurrentSnapshot;
            ITextDocument document;
            if (!documents.TryGetTextDocument(view.TextBuffer, out document)) return;
            string uri = JavaLspRpc.FileUri(document);
            if (string.IsNullOrEmpty(uri)) return;
            var source = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            active = source;
            try
            {
                JToken response = await JavaLspRpc.RequestAsync("textDocument/documentSymbol",
                    new { textDocument = new { uri } }, source.Token);
                if (disposed || view.IsClosed || source.IsCancellationRequested ||
                    version != generation || !ReferenceEquals(requested, view.TextBuffer.CurrentSnapshot))
                    return;
                var newTypes = new List<Symbol>();
                var newMembers = new List<Symbol>();
                var list = response as JArray;
                if (list != null)
                {
                    // DocumentSymbol carries children; older JDT LS clients may
                    // negotiate flat SymbolInformation with containerName instead.
                    foreach (JObject item in list.OfType<JObject>().Take(1000))
                        AddSymbol(item, requested, null, "", newTypes, newMembers, 0);
                    if (newTypes.Count > 0 && newMembers.Count == 0)
                        AddFlatMembers(list, requested, newTypes, newMembers);
                }
                if (newTypes.Count == 0 && pendingSymbolRetries < 12)
                {
                    // An imported project can initially return [] even when
                    // the LSP connection exists. Do not turn that into a
                    // permanent "Nessuna classe" state.
                    ++pendingSymbolRetries;
                    state.Text = JavaWorkspaceActivity.IsBusy
                        ? "Importazione progetto Java…"
                        : "Attendo i simboli Java…";
                    Schedule(Math.Min(3500, 900 + pendingSymbolRetries * 220));
                    return;
                }
                pendingSymbolRetries = 0;
                symbolSnapshot = requested;
                shownType = null;
                typeSymbols.Clear();
                typeSymbols.AddRange(newTypes);
                memberSymbols.Clear();
                memberSymbols.AddRange(newMembers);
                settingSelection = true;
                try
                {
                    types.ItemsSource = typeSymbols.ToArray();
                    types.IsEnabled = typeSymbols.Count != 0;
                    members.ItemsSource = noSymbols.ToArray();
                    members.IsEnabled = false;
                }
                finally { settingSelection = false; }
                state.Text = typeSymbols.Count == 0 ? "Nessuna classe/interfaccia" :
                    typeSymbols.Count + " tipi  ·  " + memberSymbols.Count + " membri";
                UpdateSelectionAtCaret();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                state.Text = "Simboli non disponibili";
                JavaLanguageStatus.LspFailure("Java/navigationBar", ex.GetType().Name);
            }
            finally
            {
                if (ReferenceEquals(active, source)) active = null;
                source.Dispose();
            }
        }

        private static bool IsType(int kind)
        {
            return kind == 5 || kind == 10 || kind == 11 || kind == 23;
        }

        private static bool IsMember(int kind)
        {
            return kind == 6 || kind == 7 || kind == 8 || kind == 9 || kind == 22;
        }

        private static void AddSymbol(JObject item, ITextSnapshot snapshot, Symbol parent,
            string prefix, List<Symbol> types, List<Symbol> members, int depth)
        {
            if (depth > 20) return;
            int kind = (int?)item["kind"] ?? -1;
            string name = (string)item["name"];
            JToken location = item["location"];
            JToken span = item["range"] ?? (location == null ? null : location["range"]);
            JToken selection = item["selectionRange"] ?? span;
            int start, end, target;
            if (string.IsNullOrWhiteSpace(name) || span == null ||
                !TryOffset(snapshot, span["start"], out start) ||
                !TryOffset(snapshot, span["end"], out end) ||
                !TryOffset(snapshot, selection == null ? null : selection["start"], out target)) return;
            if (start > end) return;
            Symbol owner = parent;
            if (IsType(kind))
            {
                owner = new Symbol { Name = name, Caption = prefix + name, Kind = kind,
                    Start = start, End = end, Target = target, Owner = parent };
                types.Add(owner);
            }
            else if (IsMember(kind) && owner != null)
            {
                members.Add(new Symbol { Name = name, Caption = name, Kind = kind,
                    Start = start, End = end, Target = target, Owner = owner });
            }
            foreach (JObject child in (item["children"] as JArray ?? new JArray()).OfType<JObject>())
                AddSymbol(child, snapshot, owner,
                    IsType(kind) ? prefix + name + "." : prefix, types, members, depth + 1);
        }

        private static void AddFlatMembers(JArray items, ITextSnapshot snapshot,
            List<Symbol> types, List<Symbol> members)
        {
            foreach (JObject item in items.OfType<JObject>().Take(1000))
            {
                int kind = (int?)item["kind"] ?? -1;
                if (!IsMember(kind)) continue;
                string name = (string)item["name"];
                string container = (string)item["containerName"];
                JToken range = item.SelectToken("location.range") ?? item["range"];
                int start, end;
                if (string.IsNullOrWhiteSpace(name) || range == null ||
                    !TryOffset(snapshot, range["start"], out start) ||
                    !TryOffset(snapshot, range["end"], out end)) continue;
                // SymbolInformation type locations often cover only the class
                // identifier, not the class body. In flat mode containerName is
                // therefore more reliable than the type's reported range.
                var owner = !string.IsNullOrEmpty(container)
                    ? types.Where(x => x.Start <= start &&
                        (string.Equals(x.Name, container, StringComparison.Ordinal) ||
                         x.Caption.EndsWith("." + container, StringComparison.Ordinal)))
                        .OrderByDescending(x => x.Start).FirstOrDefault()
                    : types.Where(x => x.Start <= start && end <= x.End)
                        .OrderBy(x => x.End - x.Start).FirstOrDefault();
                if (owner == null) continue;
                members.Add(new Symbol { Name = name, Caption = name, Kind = kind,
                    Start = start, End = end, Target = start, Owner = owner });
            }
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

        private void UpdateSelectionAtCaret()
        {
            if (disposed || settingSelection || symbolSnapshot == null ||
                !ReferenceEquals(symbolSnapshot, view.TextBuffer.CurrentSnapshot)) return;
            int caret = view.Caret.Position.BufferPosition.Position;
            var selectedType = typeSymbols.Where(x => x.Start <= caret && caret <= x.End)
                .OrderBy(x => x.End - x.Start).FirstOrDefault();
            var selectedMember = memberSymbols.Where(x => x.Start <= caret && caret <= x.End)
                .OrderBy(x => x.End - x.Start).FirstOrDefault();
            if (selectedType == null && selectedMember != null)
                selectedType = selectedMember.Owner;
            // Outside a type (e.g. final line after the closing brace),
            // show the nearest preceding type instead of leaving an empty box.
            if (selectedType == null && typeSymbols.Count > 0)
                selectedType = typeSymbols.Where(x => x.Start <= caret)
                    .OrderByDescending(x => x.Start).FirstOrDefault() ?? typeSymbols[0];
            SetSelectedType(selectedType, selectedMember);
        }

        private void SetSelectedType(Symbol selected, Symbol selectMember)
        {
            settingSelection = true;
            try
            {
                bool changed = !ReferenceEquals(shownType, selected);
                shownType = selected;
                if (changed)
                {
                    types.SelectedItem = selected;
                    var matching = memberSymbols.Where(x => ReferenceEquals(x.Owner, selected)).ToArray();
                    members.ItemsSource = matching;
                    members.IsEnabled = selected != null && matching.Length > 0;
                }
                members.SelectedItem = selectMember != null &&
                    ReferenceEquals(selectMember.Owner, selected) ? selectMember : null;
            }
            finally { settingSelection = false; }
        }

        private void OnTypeSelected(object sender, SelectionChangedEventArgs args)
        {
            if (disposed || settingSelection) return;
            var selected = types.SelectedItem as Symbol;
            SetSelectedType(selected, null);
            Navigate(selected);
        }

        private void OnMemberSelected(object sender, SelectionChangedEventArgs args)
        {
            if (disposed || settingSelection) return;
            Navigate(members.SelectedItem as Symbol);
        }

        private void Navigate(Symbol symbol)
        {
            if (symbol == null || disposed || view.IsClosed) return;
            var snapshot = view.TextBuffer.CurrentSnapshot;
            if (!ReferenceEquals(snapshot, symbolSnapshot))
            {
                Schedule(50);
                return;
            }
            int offset = Math.Max(0, Math.Min(symbol.Target, snapshot.Length));
            var point = new SnapshotPoint(snapshot, offset);
            view.Caret.MoveTo(point);
            view.ViewScroller.EnsureSpanVisible(new SnapshotSpan(point, 0));
            view.VisualElement.Focus();
        }

        public bool Enabled { get { return !disposed; } }
        public double MarginSize { get { return ActualHeight; } }
        public FrameworkElement VisualElement { get { return this; } }

        public ITextViewMargin GetTextViewMargin(string name)
        {
            return string.Equals(name, MarginName, StringComparison.OrdinalIgnoreCase) ? this : null;
        }

        private void OnClosed(object sender, EventArgs args) { Dispose(); }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            timer.Stop();
            timer.Tick -= OnTimer;
            if (active != null) active.Cancel();
            view.Caret.PositionChanged -= OnCaret;
            view.TextBuffer.Changed -= OnChanged;
            view.Closed -= OnClosed;
            JavaWorkspaceActivity.BecameIdle -= OnIdle;
            types.SelectionChanged -= OnTypeSelected;
            members.SelectionChanged -= OnMemberSelected;
        }
    }
}
