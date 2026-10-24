using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Newtonsoft.Json.Linq;

namespace JavaLanguageSupport.Editor.Navigation
{
    internal enum JavaHierarchyKind { Call, Type }

    /// <summary>
    /// Lazy LSP call/type hierarchies. All nodes come from JDT LS, not a regex
    /// scan. Only expand requested branches and bound depth to avoid cycles.
    /// </summary>
    internal sealed class JavaHierarchyWindow
    {
        private readonly JavaHierarchyKind kind;
        private readonly Action<string> log;
        private readonly System.Windows.Window window;
        private readonly TreeView tree;
        private readonly TextBlock status;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private const int MaxDepth = 12;

        private sealed class Node
        {
            internal JObject LspItem;
            internal JavaAdvancedLocation Location;
            internal bool Incoming;
            internal int Depth;
            internal bool Loaded;
            internal HashSet<string> Ancestors;
            internal TreeViewItem View;
        }

        // B2 hierarchy theme fix: explicit WPF chrome keeps foreground and
        // background readable in both VS themes, including hover, keyboard
        // focus, pressed state and inactive focus. Normal colors are dynamic
        // Visual Studio theme resources.
        private static void ThemeHierarchyCloseButton(Button button)
        {
            button.SetResourceReference(Control.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            button.SetResourceReference(Control.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            button.SetResourceReference(Control.BorderBrushProperty,
                EnvironmentColors.ToolWindowBorderBrushKey);

            bool dark = JavaLanguageSupport.Editor.Intelligence.JavaSemanticPalette.DarkTheme;
            Brush hover = new SolidColorBrush(dark
                ? Color.FromRgb(0x37, 0x45, 0x55)
                : Color.FromRgb(0xD8, 0xE9, 0xF7));
            Brush pressed = new SolidColorBrush(dark
                ? Color.FromRgb(0x28, 0x4A, 0x66)
                : Color.FromRgb(0xB9, 0xD8, 0xEF));
            Brush contrast = dark ? Brushes.White
                : (Brush)new SolidColorBrush(Color.FromRgb(0x17, 0x2D, 0x42));

            var template = new ControlTemplate(typeof(Button));
            var frame = new FrameworkElementFactory(typeof(Border));
            frame.Name = "HierarchyCloseFrame";
            frame.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
            frame.SetValue(Border.PaddingProperty, new Thickness(12, 5, 12, 5));
            frame.SetBinding(Border.BackgroundProperty, new Binding("Background") {
                RelativeSource = RelativeSource.TemplatedParent });
            frame.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") {
                RelativeSource = RelativeSource.TemplatedParent });
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.Name = "HierarchyCloseContent";
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty,
                HorizontalAlignment.Center);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty,
                VerticalAlignment.Center);
            presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            presenter.SetBinding(TextElement.ForegroundProperty, new Binding("Foreground") {
                RelativeSource = RelativeSource.TemplatedParent });
            frame.AppendChild(presenter);
            template.VisualTree = frame;

            var hoverState = new Trigger {
                Property = UIElement.IsMouseOverProperty, Value = true };
            hoverState.Setters.Add(new Setter(Border.BackgroundProperty,
                hover, "HierarchyCloseFrame"));
            hoverState.Setters.Add(new Setter(TextElement.ForegroundProperty,
                contrast, "HierarchyCloseContent"));
            template.Triggers.Add(hoverState);

            var focusState = new Trigger {
                Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
            focusState.Setters.Add(new Setter(Border.BackgroundProperty,
                hover, "HierarchyCloseFrame"));
            focusState.Setters.Add(new Setter(TextElement.ForegroundProperty,
                contrast, "HierarchyCloseContent"));
            template.Triggers.Add(focusState);

            var pressedState = new Trigger {
                Property = Button.IsPressedProperty, Value = true };
            pressedState.Setters.Add(new Setter(Border.BackgroundProperty,
                pressed, "HierarchyCloseFrame"));
            pressedState.Setters.Add(new Setter(TextElement.ForegroundProperty,
                contrast, "HierarchyCloseContent"));
            template.Triggers.Add(pressedState);

            var disabledState = new Trigger {
                Property = UIElement.IsEnabledProperty, Value = false };
            disabledState.Setters.Add(new Setter(UIElement.OpacityProperty, 0.48));
            template.Triggers.Add(disabledState);
            button.Template = template;
        }

        private JavaHierarchyWindow(JavaHierarchyKind kind, Action<string> log)
        {
            this.kind = kind;
            this.log = log;
            window = new System.Windows.Window {
                Title = kind == JavaHierarchyKind.Call ? "Java: Gerarchia chiamate" : "Java: Gerarchia tipi",
                Width = 1000, Height = 660, MinWidth = 640, MinHeight = 400,
                ResizeMode = ResizeMode.CanResizeWithGrip,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false
            };
            JavaNavigationWindowStyle.SetupWindow(window);
            var layout = new Grid { Margin = new Thickness(12) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            status = new TextBlock { Text = "Espandi un nodo per caricare i risultati da JDT LS.",
                Margin = new Thickness(0, 0, 0, 9), TextWrapping = TextWrapping.Wrap };
            JavaNavigationWindowStyle.ThemeText(status);
            Grid.SetRow(status, 0);
            layout.Children.Add(status);
            tree = new TreeView();
            JavaNavigationWindowStyle.SetupTree(tree);
            tree.MouseDoubleClick += async (sender, args) => {
                var selected = tree.SelectedItem as TreeViewItem;
                var node = selected == null ? null : selected.Tag as Node;
                if (node == null || node.Location == null) return;
                try { await JavaAdvancedNavigation.OpenAsync(node.Location, log, lifetime.Token); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { log("Java hierarchy: " + ex.Message); }
            };
            Grid.SetRow(tree, 1);
            layout.Children.Add(tree);
            var close = new Button { Content = "Chiudi", MinWidth = 100,
                Padding = new Thickness(12, 5, 12, 5), IsCancel = true,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0) };
            ThemeHierarchyCloseButton(close);
            close.Click += (sender, args) => window.Close();
            Grid.SetRow(close, 2);
            layout.Children.Add(close);
            window.Content = layout;
            window.Closed += (sender, args) => lifetime.Cancel();
        }

        internal static async Task RunAsync(JavaHierarchyKind kind, string uri,
            ITextSnapshot snapshot, int caret, Action<string> log, CancellationToken token)
        {
            string method = kind == JavaHierarchyKind.Call ?
                "textDocument/prepareCallHierarchy" : "textDocument/prepareTypeHierarchy";
            object parameters = JavaAdvancedNavigation.PositionArguments(uri, snapshot, caret);
            JToken prepared = await JavaLspRpc.RequestAsync(method, parameters, token);
            var seeds = JavaAdvancedNavigation.Objects(prepared);
            if (seeds.Count == 0 && JavaWorkspaceActivity.IsBusy)
            {
                await JavaWorkspaceActivity.WaitIfBusyAsync(2200, token);
                await Task.Delay(250, token);
                prepared = await JavaLspRpc.RequestAsync(method, parameters, token);
                seeds = JavaAdvancedNavigation.Objects(prepared);
            }

            // Standard typeHierarchy (LSP 3.17) is optional in JDT LS builds.
            // A flat list of implementations is a *fallback*, never presented as
            // an exact inheritance tree or as complete supertype information.
            List<JavaAdvancedLocation> flat = null;
            if (kind == JavaHierarchyKind.Type && seeds.Count == 0)
            {
                JToken found = await JavaLspRpc.RequestAsync("textDocument/implementation", parameters, token);
                flat = JavaAdvancedNavigation.Objects(found)
                    .Select(JavaAdvancedNavigation.Location).Where(x => x != null)
                    .GroupBy(x => x.Uri + ":" + x.Line + ":" + x.Column,
                        StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToList();
            }
            token.ThrowIfCancellationRequested();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            if (seeds.Count == 0 && (flat == null || flat.Count == 0))
            {
                string advice = kind == JavaHierarchyKind.Call ?
                    "JDT LS non ha restituito elementi per prepareCallHierarchy. " +
                    "Verifica versione del server, posizione del cursore e indicizzazione." :
                    "JDT LS non ha restituito prepareTypeHierarchy né implementazioni. " +
                    "Il supporto alla gerarchia dei tipi dipende dalla versione di JDT LS.";
                log(advice);
                System.Windows.MessageBox.Show(advice, "Java: navigazione avanzata",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var dialog = new JavaHierarchyWindow(kind, log);
            if (seeds.Count > 0)
                dialog.Populate(seeds);
            else
                dialog.PopulateFlat(JavaAdvancedNavigation.IdentifierAt(snapshot, caret), flat);
            dialog.window.Show();
        }

        private void Populate(List<JObject> seeds)
        {
            var incoming = Group(kind == JavaHierarchyKind.Call ? "Chiamanti (Incoming Calls)" : "Supertipi");
            var outgoing = Group(kind == JavaHierarchyKind.Call ? "Chiamati (Outgoing Calls)" : "Sottotipi");
            foreach (JObject seed in seeds)
            {
                AddNode(incoming, seed, true, 0, new HashSet<string>(StringComparer.Ordinal));
                AddNode(outgoing, seed, false, 0, new HashSet<string>(StringComparer.Ordinal));
            }
            incoming.IsExpanded = true;
            outgoing.IsExpanded = true;
        }

        private void PopulateFlat(string name, List<JavaAdvancedLocation> locations)
        {
            status.Text = "Gerarchia tipi LSP non disponibile. Mostro soltanto le implementazioni " +
                "restituite da JDT LS: elenco piatto, senza supertipi o relazioni ricostruite.";
            var group = Group("Implementazioni di " + name + " (fallback)");
            foreach (var location in locations)
            {
                Uri parsed;
                string label = Uri.TryCreate(location.Uri, UriKind.Absolute, out parsed)
                    ? Path.GetFileNameWithoutExtension(parsed.AbsolutePath) : location.Uri;
                var view = new TreeViewItem {
                    Header = JavaNavigationWindowStyle.TreeHeader(label +
                        " — riga " + (location.Line + 1), false),
                    Tag = new Node { Location = location }
                };
                group.Items.Add(view);
            }
            group.IsExpanded = true;
        }

        private TreeViewItem Group(string title)
        {
            var result = new TreeViewItem {
                Header = JavaNavigationWindowStyle.TreeHeader(title, true),
                IsExpanded = true
            };
            tree.Items.Add(result);
            return result;
        }

        private void AddNode(ItemsControl parent, JObject item, bool incoming,
            int depth, HashSet<string> ancestry)
        {
            var location = JavaAdvancedNavigation.Location(item);
            string name = JavaAdvancedNavigation.DisplayName(item);
            string detail = (string)item["detail"];
            if (!string.IsNullOrEmpty(detail)) name += " — " + detail;
            else if (location != null)
            {
                Uri parsed;
                if (Uri.TryCreate(location.Uri, UriKind.Absolute, out parsed))
                    name += " — " + Path.GetFileName(parsed.AbsolutePath);
            }
            string key = JavaAdvancedNavigation.Identity(item);
            bool cycle = ancestry.Contains(key);
            var visited = new HashSet<string>(ancestry, StringComparer.Ordinal) { key };
            var view = new TreeViewItem { Header = JavaNavigationWindowStyle.TreeHeader(
                cycle ? name + " (ciclo)" : name, false) };
            var node = new Node {
                View = view, LspItem = item, Location = location,
                Incoming = incoming, Depth = depth, Ancestors = visited,
                Loaded = cycle || depth >= MaxDepth
            };
            view.Tag = node;
            if (!node.Loaded)
            {
                view.Items.Add(new TreeViewItem { Header = JavaNavigationWindowStyle.TreeHeader(
                    "Espandi per caricare...", false) });
                view.Expanded += async (sender, args) => {
                    if (!ReferenceEquals(args.OriginalSource, view) || node.Loaded) return;
                    node.Loaded = true;
                    await LoadChildrenAsync(node);
                };
            }
            parent.Items.Add(view);
        }

        private async Task LoadChildrenAsync(Node node)
        {
            string method = kind == JavaHierarchyKind.Call
                ? (node.Incoming ? "callHierarchy/incomingCalls" : "callHierarchy/outgoingCalls")
                : (node.Incoming ? "typeHierarchy/supertypes" : "typeHierarchy/subtypes");
            node.View.Items.Clear();
            node.View.Items.Add(new TreeViewItem { Header = JavaNavigationWindowStyle.TreeHeader(
                "Caricamento...", false) });
            status.Text = "JDT LS: " + method;
            try
            {
                JToken response = await JavaLspRpc.RequestAsync(method,
                    new { item = node.LspItem.DeepClone() }, lifetime.Token);
                if (JavaAdvancedNavigation.Objects(response).Count == 0 &&
                    JavaWorkspaceActivity.IsBusy)
                {
                    await JavaWorkspaceActivity.WaitIfBusyAsync(1500, lifetime.Token);
                    response = await JavaLspRpc.RequestAsync(method,
                        new { item = node.LspItem.DeepClone() }, lifetime.Token);
                }
                if (!window.IsVisible || lifetime.IsCancellationRequested) return;
                node.View.Items.Clear();
                var items = JavaAdvancedNavigation.Objects(response);
                var unique = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in items.Take(200))
                {
                    JObject child = kind == JavaHierarchyKind.Call
                        ? entry[node.Incoming ? "from" : "to"] as JObject
                        : entry;
                    if (child == null || !unique.Add(JavaAdvancedNavigation.Identity(child))) continue;
                    AddNode(node.View, child, node.Incoming, node.Depth + 1, node.Ancestors);
                }
                if (node.View.Items.Count == 0)
                    node.View.Items.Add(new TreeViewItem {
                        Header = JavaNavigationWindowStyle.TreeHeader(response == null
                            ? "Nessun dato (metodo LSP non supportato o errore)"
                            : "Nessun elemento", false)
                    });
                status.Text = node.View.Items.Count + " elemento/i — " + method;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!window.IsVisible) return;
                node.View.Items.Clear();
                node.View.Items.Add(new TreeViewItem { Header = JavaNavigationWindowStyle.TreeHeader(
                    "Errore: " + ex.Message, false) });
                status.Text = "Errore durante " + method;
                log("Java hierarchy: " + ex.Message);
            }
        }
    }
}
