using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using JavaLanguageSupport.Editor.Navigation;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace JavaLanguageSupport.ProjectSystem.Packages
{
    /// <summary>
    /// F2F.3: select source root and existing starting package without entering
    /// long dotted names. Dialog is view-only; service validates and creates.
    /// </summary>
    internal sealed class JavaNewPackageDialog : DialogWindow
    {
        private sealed class SourceChoice
        {
            public string Path;
            public string Label;
            public override string ToString() { return Label; }
        }

        private sealed class PackageChoice
        {
            public string Path;
            public string Label;
            public override string ToString() { return Label; }
        }

        private readonly string _projectDirectory;
        private readonly string _initialBase;
        private readonly string _initialRoot;
        private readonly bool _projectNodeSelected;
        private readonly string _buildHint;
        private readonly ComboBox _roots;
        private readonly ComboBox _bases;
        private readonly TextBox _packageSearch;
        private List<PackageChoice> _allPackages;
        private readonly TextBox _name;
        private readonly TextBlock _packagePreview;
        private readonly TextBlock _pathPreview;
        private readonly TextBlock _feedback;
        private readonly Button _create;

        // F2F13-DYNAMIC-HEIGHT: il contenuto determina l'altezza naturale.
        private ScrollViewer _dynamicScroll;
        private StackPanel _dynamicBody;
        private bool _fitQueued;
        private bool _normalizingNewPackageName;

        // F2F11-RELATIVE-NAME: il nome inserito e' sempre relativo al package scelto.
        public string RelativePackageName
        {
            get { return RelativeToSelectedPackage(_name == null ? null : _name.Text); }
        }

        public string SelectedBaseDirectory
        {
            get
            {
                var selected = _bases.SelectedItem as PackageChoice;
                return selected == null ? null : selected.Path;
            }
        }

        // Preserve project-level creation if a conventional source root does
        // not exist yet; don't silently redirect to a different root.
        public bool UseProjectNode
        {
            get
            {
                var root = _roots.SelectedItem as SourceChoice;
                return _projectNodeSelected && root != null &&
                    Same(root.Path, _initialRoot) &&
                    Same(SelectedBaseDirectory, root.Path) &&
                    !Directory.Exists(root.Path);
            }
        }

        public JavaNewPackageDialog(string projectDirectory, string selectedBaseDirectory,
            bool projectNodeSelected, string buildHint,
            string sourceRoot, string existingPackage)
        {
            _projectDirectory = Path.GetFullPath(projectDirectory);
            _initialBase = Path.GetFullPath(selectedBaseDirectory);
            _initialRoot = Path.GetFullPath(sourceRoot);
            _projectNodeSelected = projectNodeSelected;
            _buildHint = buildHint;

            Title = "Nuovo package Java";
            // F2F9-COMPACT-PACKAGE-DIALOG: compact default; no resize, scrolling remain available.
            // F2F10-NATURAL-HEIGHT: start at the natural height of the controls.
            // The height is capped after the first layout to prevent dead space.
            Width = 680;
            SizeToContent = SizeToContent.WidthAndHeight;
            MinWidth = 520;
            MinHeight = 390;
            MaxHeight = Math.Max(390, SystemParameters.WorkArea.Height - 26);
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;
            SetResourceReference(BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            SetResourceReference(ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);

            // Actions follow the content, not the bottom edge of a tall window.
            // This removes the empty band between the preview and buttons.
            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            _create = new Button
            {
                Content = "Crea package", MinWidth = 120, Height = 30,
                Margin = new Thickness(0, 0, 10, 0),
                IsDefault = true, IsEnabled = false
            };
            var cancel = new Button
            {
                Content = "Annulla", MinWidth = 94, Height = 30,
                IsCancel = true
            };
            JavaNavigationWindowStyle.ThemeNavigationActionButton(_create);
            JavaNavigationWindowStyle.ThemeNavigationActionButton(cancel);
            _create.Click += (sender, args) =>
            {
                JavaPackageCreationPlan plan;
                string error;
                if (CanPlan(out plan, out error)) DialogResult = true;
                else { RefreshPreview(); _name.Focus(); }
            };
            cancel.Click += (sender, args) => { DialogResult = false; };
            footer.Children.Add(_create);
            footer.Children.Add(cancel);

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            var body = new StackPanel { Margin = new Thickness(22, 16, 22, 10) };
            scroll.Content = body;
            _dynamicScroll = scroll;
            _dynamicBody = body;

            var heading = Text("Crea un package Java", 18, true);
            heading.Margin = new Thickness(0, 0, 0, 3);
            body.Children.Add(heading);
            var intro = Text("Scegli i sorgenti e il package esistente da cui partire. " +
                "Scrivi soltanto il nome del nuovo package.", 11, false);
            intro.Margin = new Thickness(0, 0, 0, 13);
            body.Children.Add(intro);

            body.Children.Add(Section("RADICE DEI SORGENTI"));
            _roots = Picker("Radice sorgenti Java");
            _roots.Margin = new Thickness(0, 0, 0, 11);
            body.Children.Add(_roots);

            body.Children.Add(Section("PACKAGE DI PARTENZA"));
            var searchLabel = Text("Cerca tra i package esistenti", 12, false);
            searchLabel.Margin = new Thickness(0, 0, 0, 3);
            body.Children.Add(searchLabel);
            _packageSearch = new TextBox {
                Height = 28, Padding = new Thickness(8, 3, 8, 3),
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 5),
                ToolTip = "Filtra i package per una parte del nome."
            };
            Theme(_packageSearch);
            AutomationProperties.SetName(_packageSearch,
                "Cerca package di partenza (opzionale)");
            body.Children.Add(_packageSearch);
            _bases = Picker("Package di partenza");
            _bases.Margin = new Thickness(0, 0, 0, 3);
            body.Children.Add(_bases);
            var packageHelp = Text("Cerca per una parte del nome (ad esempio controller), " +
                "poi scegli il package dall'elenco, anche se è vuoto.", 11, false);
            packageHelp.Margin = new Thickness(0, 0, 0, 12);
            body.Children.Add(packageHelp);

            body.Children.Add(Section("NUOVO PACKAGE"));
            _name = new TextBox
            {
                Height = 28, Padding = new Thickness(9, 4, 9, 4),
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 5)
            };
            Theme(_name);
            AutomationProperties.SetName(_name, "Nome del nuovo package Java");
            body.Children.Add(_name);
            var nameHelp = Text("Esempio: repository oppure models.dto. " +
                "Non ripetere il nome del package di partenza.", 11, false);
            nameHelp.Margin = new Thickness(0, 0, 0, 11);
            body.Children.Add(nameHelp);

            var preview = new Border
            {
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 9, 12, 10)
            };
            preview.SetResourceReference(Border.BorderBrushProperty,
                EnvironmentColors.ToolWindowBorderBrushKey);
            var previewBody = new StackPanel();
            preview.Child = previewBody;
            previewBody.Children.Add(Section("ANTEPRIMA"));
            _packagePreview = Detail("—", true);
            previewBody.Children.Add(_packagePreview);
            _pathPreview = Detail("", false);
            // F2F13: mostra per intero anche un percorso su piu' righe.
            _pathPreview.TextTrimming = TextTrimming.None;
            _pathPreview.MaxHeight = double.PositiveInfinity;
            _pathPreview.Margin = new Thickness(0, 3, 0, 0);
            previewBody.Children.Add(_pathPreview);
            body.Children.Add(preview);
            _feedback = Text("", 12, false);
            _feedback.Margin = new Thickness(0, 5, 0, 0);
            body.Children.Add(_feedback);
            body.Children.Add(footer);

            // Scroll only when the window is made smaller or text requires it.
            // On taller windows, the button row stays right below the preview.
            Content = scroll;
            JavaNavigationWindowStyle.SetupWindow(this);

            FillRoots();
            _roots.SelectionChanged += (sender, args) =>
            {
                _packageSearch.Clear();
                FillPackages();
                RefreshPreview();
            };
            _bases.SelectionChanged += (sender, args) =>
            {
                RemoveRepeatedBaseFromTextBox();
                RefreshPreview();
            };
            _packageSearch.TextChanged += (sender, args) => ApplyPackageFilter();
            _name.TextChanged += (sender, args) =>
            {
                if (_normalizingNewPackageName) return;
                RemoveRepeatedBaseFromTextBox();
                RefreshPreview();
            };
            Loaded += (sender, args) =>
            {
                _name.Focus();
                System.Windows.Input.Keyboard.Focus(_name);
                // F2F13: lascia SizeToContent.Height sempre attivo.
                // La finestra cresce/diminuisce con l'anteprima.
                ScheduleDynamicHeight();
            };
            SizeChanged += (sender, args) =>
            {
                // Se cambiano le dimensioni (anche per resizing orizzontale),
                // aggiorna il limite all'altezza naturale, senza fascia vuota.
                if (Math.Abs(args.NewSize.Width - args.PreviousSize.Width) > 0.5 ||
                    args.NewSize.Height > args.PreviousSize.Height + 0.5)
                    ScheduleDynamicHeight();
            };
            RefreshPreview();
        }

        // F2F9: Visual Studio Dark's native ComboBox arrow becomes black on
        // hover/focus in some VS/Windows templates. Use a compact custom chrome
        // with an explicitly white arrow in Dark only. Light keeps the native
        // Visual Studio template unchanged.
        private static readonly ControlTemplate DarkPickerTemplate = CreateDarkPickerTemplate();

        private static ControlTemplate CreateDarkPickerTemplate()
        {
            var template = new ControlTemplate(typeof(ComboBox));
            var host = new FrameworkElementFactory(typeof(Grid));

            var chrome = new FrameworkElementFactory(typeof(Border));
            chrome.Name = "JavaPickerChrome";
            chrome.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
            chrome.SetBinding(Border.BackgroundProperty, new Binding("Background")
                { RelativeSource = RelativeSource.TemplatedParent });
            chrome.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush")
                { RelativeSource = RelativeSource.TemplatedParent });
            host.AppendChild(chrome);

            var label = new FrameworkElementFactory(typeof(TextBlock));
            label.SetBinding(TextBlock.TextProperty, new Binding("SelectedItem")
                { RelativeSource = RelativeSource.TemplatedParent });
            label.SetBinding(TextBlock.ForegroundProperty, new Binding("Foreground")
                { RelativeSource = RelativeSource.TemplatedParent });
            label.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            label.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            label.SetValue(TextBlock.MarginProperty, new Thickness(10, 0, 36, 0));
            host.AppendChild(label);

            // The transparent button covers the whole picker, so clicking its
            // text also opens the list; mouse-over never changes the arrow to black.
            var toggle = new FrameworkElementFactory(typeof(ToggleButton));
            toggle.Name = "JavaPickerToggle";
            toggle.SetValue(UIElement.FocusableProperty, false);
            toggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding("IsDropDownOpen")
                { RelativeSource = RelativeSource.TemplatedParent, Mode = BindingMode.TwoWay });
            var toggleTemplate = new ControlTemplate(typeof(ToggleButton));
            var hitArea = new FrameworkElementFactory(typeof(Grid));
            hitArea.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
            var arrow = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
            arrow.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M 0,0 L 4,4 L 8,0"));
            arrow.SetValue(System.Windows.Shapes.Shape.StrokeProperty, Brushes.White);
            arrow.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.7);
            arrow.SetValue(System.Windows.Shapes.Shape.StrokeStartLineCapProperty, PenLineCap.Round);
            arrow.SetValue(System.Windows.Shapes.Shape.StrokeEndLineCapProperty, PenLineCap.Round);
            arrow.SetValue(FrameworkElement.HorizontalAlignmentProperty,
                HorizontalAlignment.Right);
            arrow.SetValue(FrameworkElement.VerticalAlignmentProperty,
                VerticalAlignment.Center);
            arrow.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 12, 0));
            hitArea.AppendChild(arrow);
            toggleTemplate.VisualTree = hitArea;
            toggle.SetValue(Control.TemplateProperty, toggleTemplate);
            host.AppendChild(toggle);

            var popup = new FrameworkElementFactory(typeof(Popup));
            popup.Name = "PART_Popup";
            popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
            popup.SetValue(Popup.AllowsTransparencyProperty, true);
            popup.SetValue(Popup.StaysOpenProperty, false);
            popup.SetBinding(Popup.PlacementTargetProperty, new Binding()
                { RelativeSource = RelativeSource.TemplatedParent });
            popup.SetBinding(Popup.IsOpenProperty, new Binding("IsDropDownOpen")
                { RelativeSource = RelativeSource.TemplatedParent, Mode = BindingMode.TwoWay });

            var popupBorder = new FrameworkElementFactory(typeof(Border));
            popupBorder.Name = "JavaPickerPopupBorder";
            popupBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            popupBorder.SetBinding(Border.BackgroundProperty, new Binding("Background")
                { RelativeSource = RelativeSource.TemplatedParent });
            popupBorder.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush")
                { RelativeSource = RelativeSource.TemplatedParent });
            popupBorder.SetBinding(FrameworkElement.WidthProperty, new Binding("ActualWidth")
                { RelativeSource = RelativeSource.TemplatedParent });

            var dropdownScroll = new FrameworkElementFactory(typeof(ScrollViewer));
            dropdownScroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty,
                ScrollBarVisibility.Auto);
            dropdownScroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty,
                ScrollBarVisibility.Disabled);
            dropdownScroll.SetValue(ScrollViewer.CanContentScrollProperty, true);
            dropdownScroll.SetBinding(FrameworkElement.MaxHeightProperty,
                new Binding("MaxDropDownHeight")
                { RelativeSource = RelativeSource.TemplatedParent });
            var items = new FrameworkElementFactory(typeof(ItemsPresenter));
            dropdownScroll.AppendChild(items);
            popupBorder.AppendChild(dropdownScroll);
            popup.AppendChild(popupBorder);
            host.AppendChild(popup);

            template.VisualTree = host;
            return template;
        }

        // F2F4-THEMED-PICKER: WPF Popup is not painted by ComboBox.Background.
        // Apply explicit colors to BOTH the collapsed control and its popup
        // container/items, including highlighted and selected rows.
        private static ComboBox Picker(string accessibleName)
        {
            var picker = new ComboBox
            {
                Height = 30, IsEditable = false,
                IsTextSearchEnabled = true, MaxDropDownHeight = 285,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(6, 1, 6, 1)
            };
            Theme(picker);
            AutomationProperties.SetName(picker, accessibleName);
            picker.Loaded += (sender, args) => ApplyPickerTheme(picker);
            picker.DropDownOpened += (sender, args) => ApplyPickerTheme(picker);
            return picker;
        }

        private static void ApplyPickerTheme(ComboBox picker)
        {
            // Read the *active Visual Studio* palette rather than Windows colors.
            // Re-evaluate when opened, so changing theme takes effect on re-open.
            System.Drawing.Color backgroundColor;
            System.Drawing.Color textColor;
            System.Drawing.Color borderColor;
            try
            {
                backgroundColor = VSColorTheme.GetThemedColor(
                    EnvironmentColors.ToolWindowBackgroundColorKey);
                textColor = VSColorTheme.GetThemedColor(
                    EnvironmentColors.ToolWindowTextColorKey);
                borderColor = VSColorTheme.GetThemedColor(
                    EnvironmentColors.ToolWindowBorderColorKey);
            }
            catch (Exception)
            {
                backgroundColor = System.Drawing.SystemColors.Window;
                textColor = System.Drawing.SystemColors.WindowText;
                borderColor = System.Drawing.SystemColors.ActiveBorder;
            }
            var background = new SolidColorBrush(Color.FromRgb(
                backgroundColor.R, backgroundColor.G, backgroundColor.B));
            var text = new SolidColorBrush(Color.FromRgb(
                textColor.R, textColor.G, textColor.B));
            var borderBrush = new SolidColorBrush(Color.FromRgb(
                borderColor.R, borderColor.G, borderColor.B));
            bool dark = (backgroundColor.R * 299 + backgroundColor.G * 587 +
                backgroundColor.B * 114) < 128000;
            var hoverBackground = new SolidColorBrush(dark
                ? Color.FromRgb(0x36, 0x49, 0x60) : Color.FromRgb(0xD8, 0xE9, 0xF7));
            var selectedBackground = new SolidColorBrush(dark
                ? Color.FromRgb(0x26, 0x4F, 0x78) : Color.FromRgb(0xC5, 0xDE, 0xF5));
            var selectionText = dark ? Brushes.White :
                (Brush)new SolidColorBrush(Color.FromRgb(0x17, 0x2D, 0x42));

            if (dark)
            {
                if (!ReferenceEquals(picker.Template, DarkPickerTemplate))
                    picker.Template = DarkPickerTemplate;
            }
            else if (ReferenceEquals(picker.Template, DarkPickerTemplate))
            {
                picker.ClearValue(Control.TemplateProperty);
            }

            picker.Background = background;
            picker.Foreground = text;
            picker.BorderBrush = borderBrush;
            // Built-in WPF templates use system window/control resource keys for
            // the drop-down even when the ComboBox control itself is themed.
            picker.Resources[SystemColors.WindowBrushKey] = background;
            picker.Resources[SystemColors.WindowTextBrushKey] = text;
            picker.Resources[SystemColors.ControlBrushKey] = background;
            picker.Resources[SystemColors.ControlTextBrushKey] = text;
            picker.Resources[SystemColors.HighlightBrushKey] = selectedBackground;
            picker.Resources[SystemColors.HighlightTextBrushKey] = selectionText;

            var itemStyle = new Style(typeof(ComboBoxItem));
            itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, background));
            itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, text));
            itemStyle.Setters.Add(new Setter(Control.BorderBrushProperty, borderBrush));
            itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 3, 8, 3)));
            var over = new Trigger
            {
                Property = ComboBoxItem.IsHighlightedProperty, Value = true
            };
            over.Setters.Add(new Setter(Control.BackgroundProperty, hoverBackground));
            over.Setters.Add(new Setter(Control.ForegroundProperty, selectionText));
            itemStyle.Triggers.Add(over);
            var selected = new Trigger
            {
                Property = ComboBoxItem.IsSelectedProperty, Value = true
            };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, selectedBackground));
            selected.Setters.Add(new Setter(Control.ForegroundProperty, selectionText));
            itemStyle.Triggers.Add(selected);
            picker.ItemContainerStyle = itemStyle;

            // Give the item text a known foreground in popup as well as when
            // collapsed (avoids a VS theme template overriding item text).
            var textBlock = new FrameworkElementFactory(typeof(TextBlock));
            textBlock.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("."));
            textBlock.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            textBlock.SetValue(TextBlock.ForegroundProperty, text);
            picker.ItemTemplate = new DataTemplate { VisualTree = textBlock };

            // Some VS/Windows themes paint the collapsed ComboBox chrome with
            // a separate Border, ignoring ComboBox.Background. Repaint that
            // border as well so selected text is legible with the popup shut.
            picker.ApplyTemplate();
            var chrome = FindPopupBorder(picker, 0);
            if (chrome != null)
            {
                chrome.Background = background;
                chrome.BorderBrush = borderBrush;
            }
            // Popup.Child is a separate visual subtree in the standard VS/WPF
            // ComboBox. Explicitly repaint its DropDownBorder if present.
            var popup = picker.Template == null ? null :
                picker.Template.FindName("PART_Popup", picker) as System.Windows.Controls.Primitives.Popup;
            if (popup != null && popup.Child != null)
            {
                var popupBorder = FindPopupBorder(popup.Child, 0);
                if (popupBorder != null)
                {
                    popupBorder.Background = background;
                    popupBorder.BorderBrush = borderBrush;
                }
            }
        }

        private static Border FindPopupBorder(DependencyObject node, int depth)
        {
            if (node is Border) return (Border)node;
            if (node == null || depth > 5) return null;
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                Border found = FindPopupBorder(
                    System.Windows.Media.VisualTreeHelper.GetChild(node, i), depth + 1);
                if (found != null) return found;
            }
            return null;
        }

        private static void Theme(Control control)
        {
            control.SetResourceReference(Control.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            control.SetResourceReference(Control.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            control.SetResourceReference(Control.BorderBrushProperty,
                EnvironmentColors.ToolWindowBorderBrushKey);
        }

        private static bool Same(string a, string b)
        {
            return !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
                string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase);
        }

        private void FillRoots()
        {
            var choices = new List<SourceChoice>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in JavaSourceRootResolver.Discover(_projectDirectory, _buildHint))
            {
                // Don't show source roots that cannot be used right now;
                // except the initially resolved root of a project-level command.
                if (!Directory.Exists(root.FullPath) && !Same(root.FullPath, _initialRoot))
                    continue;
                if (!seen.Add(root.FullPath.TrimEnd('\\', '/'))) continue;
                string kind = root.Kind == JavaSourceRootKind.Main ? "Principale" :
                    root.Kind == JavaSourceRootKind.Test ? "Test" :
                    root.Kind == JavaSourceRootKind.Plain ? "Semplice" : "Aggiuntiva";
                string relative = DisplayPath(root.FullPath);
                choices.Add(new SourceChoice { Path = root.FullPath,
                    Label = kind + "  ·  " + relative });
            }
            if (!choices.Any(c => Same(c.Path, _initialRoot)))
                choices.Insert(0, new SourceChoice {
                    Path = _initialRoot, Label = "Sorgenti  ·  " + DisplayPath(_initialRoot) });
            _roots.ItemsSource = choices;
            _roots.SelectedItem = choices.FirstOrDefault(c => Same(c.Path, _initialRoot))
                ?? choices.FirstOrDefault();
            FillPackages();
        }

        private string DisplayPath(string path)
        {
            string prefix = _projectDirectory.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? path.Substring(prefix.Length) : path;
        }

        private void FillPackages()
        {
            var root = _roots.SelectedItem as SourceChoice;
            if (root == null) { _bases.ItemsSource = null; return; }
            var choices = new List<PackageChoice> {
                new PackageChoice { Path = root.Path, Label = "(package predefinito / radice)" }
            };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            seen.Add(root.Path.TrimEnd('\\', '/'));
            var pending = new Stack<string>();
            if (Directory.Exists(root.Path)) pending.Push(root.Path);
            const int maxPackages = 2000;
            const int maxDepth = 28;
            while (pending.Count > 0 && choices.Count < maxPackages)
            {
                string parent = pending.Pop();
                string[] children;
                try { children = Directory.GetDirectories(parent); }
                catch (Exception ex) when (ex is IOException ||
                    ex is UnauthorizedAccessException) { continue; }
                foreach (string dir in children.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    if (choices.Count >= maxPackages) break;
                    string segment = Path.GetFileName(dir);
                    string invalid;
                    if (!JavaPackageResolver.TryValidatePackageName(segment, out invalid))
                        continue;
                    try
                    {
                        if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                            continue; // Never follow junctions/symlinks out of the root.
                    }
                    catch (Exception ex) when (ex is IOException ||
                        ex is UnauthorizedAccessException) { continue; }
                    string rel = dir.Substring(root.Path.TrimEnd('\\', '/').Length)
                        .TrimStart('\\', '/');
                    if (rel.Split(Path.DirectorySeparatorChar).Length > maxDepth) continue;
                    if (!seen.Add(dir.TrimEnd('\\', '/'))) continue;
                    choices.Add(new PackageChoice {
                        Path = dir,
                        Label = rel.Replace(Path.DirectorySeparatorChar, '.')
                                   .Replace(Path.AltDirectorySeparatorChar, '.')
                    });
                    pending.Push(dir);
                }
            }
            choices = choices.Take(1).Concat(choices.Skip(1)
                .OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase)).ToList();
            _allPackages = choices;
            ApplyPackageFilter();
            _bases.ToolTip = "Seleziona un package esistente (o la radice).";
        }

        private void ApplyPackageFilter()
        {
            if (_allPackages == null) return;
            string filter = _packageSearch.Text.Trim();
            PackageChoice previous = _bases.SelectedItem as PackageChoice;
            var visible = filter.Length == 0 ? _allPackages : _allPackages
                .Where(c => c.Label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            _bases.ItemsSource = visible;
            _bases.SelectedItem = visible.FirstOrDefault(c =>
                previous != null && Same(c.Path, previous.Path))
                ?? (filter.Length == 0
                    ? visible.FirstOrDefault(c => Same(c.Path, _initialBase))
                    : null)
                ?? visible.FirstOrDefault();
            RefreshPreview();
        }

        // F2F11-RELATIVE-NAME: calcola il package effettivo usando i percorsi
        // reali, senza dipendere dalle etichette della ComboBox.
        // Esempio: radice=src/main/java, base=com.example,
        // input=com.example.controllers.rest => controllers.rest.
        private string SelectedPackageName()
        {
            var root = _roots == null ? null : _roots.SelectedItem as SourceChoice;
            var selected = _bases == null ? null : _bases.SelectedItem as PackageChoice;
            if (root == null || selected == null ||
                string.IsNullOrEmpty(root.Path) || string.IsNullOrEmpty(selected.Path))
                return string.Empty;

            string rootPath = root.Path.TrimEnd('\\', '/');
            string selectedPath = selected.Path.TrimEnd('\\', '/');
            string prefix = rootPath + Path.DirectorySeparatorChar;
            if (!selectedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return string.Empty; // Package predefinito o selezione incoerente.

            return selectedPath.Substring(prefix.Length).Replace('\\', '.').Replace('/', '.');
        }

        private string RelativeToSelectedPackage(string entered)
        {
            string value = (entered ?? string.Empty).Trim();
            string parent = SelectedPackageName();
            if (parent.Length == 0) return value;

            // Se l'utente scrive solo il parent non esiste un nuovo package:
            // il piano riceve un nome vuoto, disabilitando "Crea package".
            if (string.Equals(value, parent, StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            // Rimuovere solo i segmenti completi: "com.examplex" NON e'
            // lo stesso prefisso di "com.example".
            string prefix = parent + ".";
            return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? value.Substring(prefix.Length) : value;
        }

        private void RemoveRepeatedBaseFromTextBox()
        {
            if (_normalizingNewPackageName || _name == null)
                return;

            string parent = SelectedPackageName();
            if (parent.Length == 0)
                return;

            string current = _name.Text.Trim();
            string prefix = parent + ".";

            if (!current.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return;

            string relative = current.Substring(prefix.Length);

            // Non normalizzare finché l'utente non ha scritto
            // almeno un carattere dopo il package di partenza.
            if (relative.Length == 0)
                return;

            int caret = _name.CaretIndex;
            _normalizingNewPackageName = true;
            try
            {
                _name.Text = relative;

                _name.CaretIndex = Math.Max(0,Math.Min(relative.Length, caret - prefix.Length));
            }
            finally
            {
                _normalizingNewPackageName = false;
            }
        }

        private bool CanPlan(out JavaPackageCreationPlan plan, out string error)
        {
            plan = null;
            error = null;
            if (string.IsNullOrEmpty(SelectedBaseDirectory))
            {
                error = "Seleziona un package di partenza.";
                return false;
            }
            return JavaPackageCreationService.TryPlan(_projectDirectory,
                SelectedBaseDirectory, UseProjectNode, RelativePackageName, _buildHint,
                out plan, out error);
        }

        private void RefreshPreview()
        {
            if (_create == null || _name == null || _bases == null) return;
            JavaPackageCreationPlan plan;
            string error;
            if (CanPlan(out plan, out error))
            {
                _packagePreview.Text = plan.FullPackageName;
                _packagePreview.ToolTip = plan.FullPackageName;
                _pathPreview.Text = plan.TargetDirectory;
                _pathPreview.ToolTip = plan.TargetDirectory;
                _feedback.Text = "La cartella sarà inclusa nel progetto Java.";
                _create.IsEnabled = true;
            }
            else
            {
                _packagePreview.Text = "—";
                _packagePreview.ToolTip = null;
                _pathPreview.Text = "";
                _pathPreview.ToolTip = null;
                _feedback.Text = string.IsNullOrWhiteSpace(_name.Text)
                    ? "Inserisci il nome per visualizzare il risultato."
                    : (error ?? "Nome package non valido.");
                _create.IsEnabled = false;
            }
            ScheduleDynamicHeight();
        }

        // F2F13-DYNAMIC-HEIGHT: misuriamo lo StackPanel con altezza infinita;
        // misurare il solo ScrollViewer non funziona quando e' gia' scrollabile.
        // La finestra resta SizeToContent.Height finche' l'utente non la ridimensiona
        // manualmente. Se la riduce, la scrollbar Auto continua a funzionare.
        private void ScheduleDynamicHeight()
        {
            if (!IsLoaded || _dynamicBody == null || _dynamicScroll == null || _fitQueued)
                return;
            _fitQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _fitQueued = false;
                if (!IsLoaded || _dynamicScroll.ActualWidth <= 0) return;
                // Larghezza disponibile per il contenuto, mantenendo i margini.
                double width = _dynamicScroll.ActualWidth;
                _dynamicBody.Measure(new Size(width, double.PositiveInfinity));
                double chrome = Math.Max(0, ActualHeight - _dynamicScroll.ActualHeight);
                double fullHeight = Math.Ceiling(_dynamicBody.DesiredSize.Height + chrome + 2);
                double screenLimit = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 26);
                double newLimit = Math.Max(MinHeight, Math.Min(screenLimit, fullHeight));
                if (Math.Abs(MaxHeight - newLimit) > 1)
                    MaxHeight = newLimit;
                // Con SizeToContent.Height la finestra si aggiorna da sola.
                // In modalita' manuale correggiamo solo un eccesso di altezza,
                // rispettando l'eventuale ridimensionamento verso il basso.
                if (SizeToContent == SizeToContent.Manual && ActualHeight > newLimit + 1)
                    Height = newLimit;
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private static TextBlock Text(string value, double size, bool bold)
        {
            var block = new TextBlock
            {
                Text = value, FontSize = size,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap
            };
            block.SetResourceReference(TextBlock.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            return block;
        }

        private static TextBlock Section(string value)
        {
            var block = Text(value, 11, true);
            block.Margin = new Thickness(0, 0, 0, 5);
            return block;
        }

        private static TextBlock Detail(string value, bool bold)
        {
            var block = Text(value, bold ? 13 : 11, bold);
            block.TextTrimming = TextTrimming.CharacterEllipsis;
            block.MaxHeight = bold ? 42 : 50;
            block.ToolTip = value;
            return block;
        }
    }
}
