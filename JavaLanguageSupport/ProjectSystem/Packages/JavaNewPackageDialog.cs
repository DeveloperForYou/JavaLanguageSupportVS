using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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

        public string RelativePackageName { get { return _name.Text.Trim(); } }

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
            Width = 720;
            Height = 590;
            MinWidth = 540;
            MinHeight = 480;
            MaxHeight = Math.Max(480, SystemParameters.WorkArea.Height - 26);
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;
            SetResourceReference(BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            SetResourceReference(ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);

            var layout = new DockPanel { LastChildFill = true };
            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(22, 12, 24, 17)
            };
            DockPanel.SetDock(footer, Dock.Bottom);
            layout.Children.Add(footer);

            _create = new Button
            {
                Content = "Crea package", MinWidth = 135, Height = 32,
                Margin = new Thickness(0, 0, 10, 0),
                IsDefault = true, IsEnabled = false
            };
            var cancel = new Button
            {
                Content = "Annulla", MinWidth = 108, Height = 32,
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
            layout.Children.Add(scroll);
            var body = new StackPanel { Margin = new Thickness(24, 22, 24, 12) };
            scroll.Content = body;

            var heading = Text("Crea un package Java", 21, true);
            heading.Margin = new Thickness(0, 0, 0, 6);
            body.Children.Add(heading);
            var intro = Text("Scegli i sorgenti e il package esistente da cui partire. " +
                "Scrivi soltanto il nome del nuovo package.", 12, false);
            intro.Margin = new Thickness(0, 0, 0, 21);
            body.Children.Add(intro);

            body.Children.Add(Section("1. RADICE DEI SORGENTI"));
            _roots = Picker("Radice sorgenti Java");
            _roots.Margin = new Thickness(0, 0, 0, 15);
            body.Children.Add(_roots);

            body.Children.Add(Section("2. PACKAGE DI PARTENZA"));
            var searchLabel = Text("Cerca tra i package esistenti", 12, false);
            searchLabel.Margin = new Thickness(0, 0, 0, 5);
            body.Children.Add(searchLabel);
            _packageSearch = new TextBox {
                Height = 31, Padding = new Thickness(8, 4, 8, 4),
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 7),
                ToolTip = "Filtra i package per una parte del nome."
            };
            Theme(_packageSearch);
            AutomationProperties.SetName(_packageSearch,
                "Cerca package di partenza (opzionale)");
            body.Children.Add(_packageSearch);
            _bases = Picker("Package di partenza");
            _bases.Margin = new Thickness(0, 0, 0, 5);
            body.Children.Add(_bases);
            var packageHelp = Text("Cerca per una parte del nome (ad esempio controller), " +
                "poi scegli il package dall'elenco, anche se è vuoto.", 11, false);
            packageHelp.Margin = new Thickness(0, 0, 0, 19);
            body.Children.Add(packageHelp);

            body.Children.Add(Section("3. NUOVO PACKAGE"));
            _name = new TextBox
            {
                Height = 34, Padding = new Thickness(9, 5, 9, 5),
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 5)
            };
            Theme(_name);
            AutomationProperties.SetName(_name, "Nome del nuovo package Java");
            body.Children.Add(_name);
            var nameHelp = Text("Esempio: repository oppure models.dto. " +
                "Non ripetere il nome del package di partenza.", 11, false);
            nameHelp.Margin = new Thickness(0, 0, 0, 17);
            body.Children.Add(nameHelp);

            var preview = new Border
            {
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 11, 14, 12)
            };
            preview.SetResourceReference(Border.BorderBrushProperty,
                EnvironmentColors.ToolWindowBorderBrushKey);
            var previewBody = new StackPanel();
            preview.Child = previewBody;
            previewBody.Children.Add(Section("ANTEPRIMA"));
            _packagePreview = Detail("—", true);
            previewBody.Children.Add(_packagePreview);
            _pathPreview = Detail("", false);
            _pathPreview.Margin = new Thickness(0, 6, 0, 0);
            previewBody.Children.Add(_pathPreview);
            body.Children.Add(preview);
            _feedback = Text("", 12, false);
            _feedback.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(_feedback);

            Content = layout;
            JavaNavigationWindowStyle.SetupWindow(this);

            FillRoots();
            _roots.SelectionChanged += (sender, args) =>
            {
                _packageSearch.Clear();
                FillPackages();
                RefreshPreview();
            };
            _bases.SelectionChanged += (sender, args) => RefreshPreview();
            _packageSearch.TextChanged += (sender, args) => ApplyPackageFilter();
            _name.TextChanged += (sender, args) => RefreshPreview();
            Loaded += (sender, args) =>
            {
                _name.Focus();
                System.Windows.Input.Keyboard.Focus(_name);
            };
            RefreshPreview();
        }

        // F2F4-THEMED-PICKER: WPF Popup is not painted by ComboBox.Background.
        // Apply explicit colors to BOTH the collapsed control and its popup
        // container/items, including highlighted and selected rows.
        private static ComboBox Picker(string accessibleName)
        {
            var picker = new ComboBox
            {
                Height = 33, IsEditable = false,
                IsTextSearchEnabled = true, MaxDropDownHeight = 285,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(6, 2, 6, 2)
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
            itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 5, 9, 5)));
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
                SelectedBaseDirectory, UseProjectNode, _name.Text, _buildHint,
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
            block.Margin = new Thickness(0, 0, 0, 7);
            return block;
        }

        private static TextBlock Detail(string value, bool bold)
        {
            var block = Text(value, bold ? 15 : 12, bold);
            block.TextTrimming = TextTrimming.CharacterEllipsis;
            block.MaxHeight = bold ? 42 : 50;
            block.ToolTip = value;
            return block;
        }
    }
}
