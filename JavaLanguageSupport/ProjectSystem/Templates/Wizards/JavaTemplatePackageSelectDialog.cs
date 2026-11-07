
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using JavaLanguageSupport.ProjectSystem.Packages;
using JavaLanguageSupport.Editor.Navigation;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace JavaLanguageSupport.ProjectSystem.Templates.Wizards
{
    /// <summary>
    /// F2G.1: choose an existing Java package for a native item template.
    /// This dialog never creates or moves files. All locations are discovered
    /// from the project's Maven/Gradle/plain source roots.
    /// </summary>
    internal sealed class JavaTemplatePackageSelectDialog : DialogWindow
    {
        private sealed class Choice
        {
            public string Path { get; set; }
            public string Label { get; set; }
            public override string ToString() { return Label; }
        }

        // F2G2-PACKAGE-IN-ITEM-WIZARD: needed to register empty CPS folders.
        private readonly string _projectFile;
        private readonly string _projectDirectory;
        private readonly string _buildHint;
        private readonly string _initialDirectory;
        // F2G1A-NAME-FIX: editable name fallback if CPS provides no name token.
        private readonly TextBox _javaName;
        private readonly bool _allowSpecialDescriptors;
        private readonly ListBox _roots;
        private readonly ListBox _packages;
        private readonly TextBox _search;
        private readonly TextBlock _packageName;
        private readonly TextBlock _destination;
        private readonly TextBlock _message;
        private readonly Button _confirm;
        private List<Choice> _allPackages = new List<Choice>();

        internal string SelectedJavaName { get { return _javaName.Text.Trim(); } }

        internal string SelectedDirectory
        {
            get
            {
                var choice = _packages.SelectedItem as Choice;
                return choice == null ? null : choice.Path;
            }
        }

        internal JavaTemplatePackageSelectDialog(string projectFile,
            string projectDirectory, string initialDirectory, string buildHint, string fileName,
            bool allowSpecialDescriptors)
        {
            _projectFile = Path.GetFullPath(projectFile);
            _projectDirectory = Path.GetFullPath(projectDirectory);
            _initialDirectory = Path.GetFullPath(initialDirectory);
            _buildHint = buildHint;
            _allowSpecialDescriptors = allowSpecialDescriptors;

            Title = "Destinazione elemento Java";
            Width = 710;
            Height = 540;
            MinWidth = 610;
            MinHeight = 420;
            MaxHeight = Math.Max(420, SystemParameters.WorkArea.Height - 28);
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 12;
            SetResourceReference(BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            SetResourceReference(ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);

            var body = new DockPanel { LastChildFill = true };
            Content = body;
            var footer = new StackPanel {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(16, 8, 18, 12)
            };
            DockPanel.SetDock(footer, Dock.Bottom);
            body.Children.Add(footer);
            _confirm = new Button {
                Content = "Crea elemento", MinWidth = 124, Height = 30,
                Margin = new Thickness(0, 0, 8, 0), IsDefault = true, IsEnabled = false
            };
            var cancel = new Button {
                Content = "Annulla", MinWidth = 105, Height = 30, IsCancel = true
            };
            JavaNavigationWindowStyle.ThemeNavigationActionButton(_confirm);
            JavaNavigationWindowStyle.ThemeNavigationActionButton(cancel);
            _confirm.Click += (sender, args) => {
                if (_confirm.IsEnabled) DialogResult = true;
            };
            cancel.Click += (sender, args) => { DialogResult = false; };
            footer.Children.Add(_confirm);
            footer.Children.Add(cancel);

            var content = new Grid { Margin = new Thickness(22, 18, 22, 3) };
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.Children.Add(content);

            var title = Label("Seleziona il package di destinazione", 17, true);
            title.Margin = new Thickness(0, 0, 0, 4);
            Grid.SetRow(title, 0);
            content.Children.Add(title);
            var subtitle = Label("Scegli il nome e il package di destinazione del file Java.", 12, false);
            subtitle.Margin = new Thickness(0, 0, 0, 12);
            Grid.SetRow(subtitle, 1);
            content.Children.Add(subtitle);

            var nameBlock = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            nameBlock.Children.Add(Label("NOME FILE JAVA", 11, true));
            string proposed = fileName ?? string.Empty;
            if (proposed.EndsWith(".java", StringComparison.OrdinalIgnoreCase))
                proposed = proposed.Substring(0, proposed.Length - 5);
            _javaName = new TextBox {
                Height = 30, Text = proposed, Padding = new Thickness(8, 4, 8, 4),
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 5, 0, 0),
                ToolTip = "Nome semplice della classe o del file (es. Customer)"
            };
            Theme(_javaName);
            AutomationProperties.SetName(_javaName, "Nome del nuovo file Java");
            nameBlock.Children.Add(_javaName);
            Grid.SetRow(nameBlock, 2);
            content.Children.Add(nameBlock);

            var columns = new Grid { MinHeight = 190 };
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(215) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(columns, 3);
            content.Children.Add(columns);

            var left = new DockPanel { LastChildFill = true };
            Grid.SetColumn(left, 0);
            columns.Children.Add(left);
            var rootLabel = Label("RADICE DEI SORGENTI", 11, true);
            rootLabel.Margin = new Thickness(0, 0, 0, 7);
            DockPanel.SetDock(rootLabel, Dock.Top);
            left.Children.Add(rootLabel);
            _roots = MakeList("Radici Java disponibili");
            left.Children.Add(_roots);

            var right = new DockPanel { LastChildFill = true };
            Grid.SetColumn(right, 2);
            columns.Children.Add(right);
            // Place the secondary action next to the package header so it
            // does not take vertical space from the scrollable list.
            var packageHeader = new DockPanel { LastChildFill = true,
                Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(packageHeader, Dock.Top);
            right.Children.Add(packageHeader);
            var createPackage = new Button { Content = "Nuovo package...",
                Height = 26, MinWidth = 124, Margin = new Thickness(8, 0, 0, 0) };
            JavaNavigationWindowStyle.ThemeNavigationActionButton(createPackage);
            DockPanel.SetDock(createPackage, Dock.Right);
            packageHeader.Children.Add(createPackage);
            var packageLabel = Label("PACKAGE ESISTENTE", 11, true);
            packageLabel.VerticalAlignment = VerticalAlignment.Center;
            packageHeader.Children.Add(packageLabel);
            createPackage.Click += (sender, args) => CreatePackageFromItemWizard();
            _search = new TextBox {
                Height = 28, Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 0, 7),
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "Filtra i package esistenti"
            };
            Theme(_search);
            AutomationProperties.SetName(_search, "Filtra i package Java");
            DockPanel.SetDock(_search, Dock.Top);
            right.Children.Add(_search);
            _packages = MakeList("Package Java di destinazione");
            right.Children.Add(_packages);

            var preview = new Border {
                Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(12, 9, 12, 10),
                CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1)
            };
            preview.SetResourceReference(Border.BorderBrushProperty,
                EnvironmentColors.ToolWindowBorderBrushKey);
            Grid.SetRow(preview, 4);
            content.Children.Add(preview);
            var stack = new StackPanel();
            preview.Child = stack;
            var previewLabel = Label("ANTEPRIMA", 11, true);
            previewLabel.Margin = new Thickness(0, 0, 0, 5);
            stack.Children.Add(previewLabel);
            _packageName = Label("", 12, true);
            stack.Children.Add(_packageName);
            _destination = Label("", 11, false);
            _destination.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(_destination);
            _message = Label("", 11, false);
            _message.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(_message);

            JavaNavigationWindowStyle.SetupWindow(this);
            _roots.SelectionChanged += (sender, args) => FillPackages();
            _packages.SelectionChanged += (sender, args) => RefreshPreview();
            _search.TextChanged += (sender, args) => FilterPackages();
            _javaName.TextChanged += (sender, args) => RefreshPreview();
            FillRoots();
            Loaded += (sender, args) => {
                if (string.IsNullOrWhiteSpace(_javaName.Text)) _javaName.Focus();
                else _search.Focus();
            };
        }

        private void CreatePackageFromItemWizard()
        {
            var root = _roots.SelectedItem as Choice;
            if (root == null) return;
            var selected = _packages.SelectedItem as Choice;
            string baseDirectory = selected != null ? selected.Path : root.Path;
            JavaPackageInfo info;
            string error;
            if (!JavaPackageResolver.TryResolveForDirectory(_projectDirectory,
                baseDirectory, out info, out error, _buildHint))
            {
                VsShellUtilities.ShowMessageBox(ServiceProvider.GlobalProvider, error ?? "Package non valido.",
                    "Nuovo package Java", OLEMSGICON.OLEMSGICON_WARNING,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }
            var dialog = new JavaNewPackageDialog(_projectDirectory,
                baseDirectory, false, _buildHint,
                info.SourceRoot.FullPath, info.Name);
            if (dialog.ShowDialog() != true) return;

            JavaPackageCreationPlan plan;
            if (string.IsNullOrWhiteSpace(dialog.SelectedBaseDirectory) ||
                !JavaPackageCreationService.TryPlan(_projectDirectory,
                    dialog.SelectedBaseDirectory, dialog.UseProjectNode,
                    dialog.RelativePackageName, _buildHint, out plan, out error))
            {
                VsShellUtilities.ShowMessageBox(ServiceProvider.GlobalProvider, error ?? "Nome package non valido.",
                    "Nuovo package Java", OLEMSGICON.OLEMSGICON_WARNING,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }
            try
            {
                // F2I5-CPS-CREATION-SYNC
                string[] newlyCreated = JavaWorkspaceCreationSync.MissingDirectories(
                    _projectFile, plan.TargetDirectory);
                Directory.CreateDirectory(plan.TargetDirectory);
                string includeError;
                bool included = JavaEmptyPackageProjectItems.TryInclude(_projectFile,
                    plan.TargetDirectory, out includeError);
                if (!included)
                {
                    VsShellUtilities.ShowMessageBox(ServiceProvider.GlobalProvider,
                        "Directory creata, ma non inclusa come Folder nel .javaproj:\n" + includeError,
                        "Nuovo package Java", OLEMSGICON.OLEMSGICON_WARNING,
                        OLEMSGBUTTON.OLEMSGBUTTON_OK,
                        OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                }
                if (included)
                    JavaWorkspaceCreationSync.QueueCreatedDirectories(
                        _projectFile, newlyCreated);
                // Keep the new Java file name and refresh the package list.
                // The nested dialog may have selected a different source root.
                _search.Text = string.Empty;
                var newRoot = _roots.Items.OfType<Choice>()
                    .FirstOrDefault(c => Same(c.Path, plan.SourceRootDirectory));
                if (newRoot != null && !Same(root.Path, newRoot.Path))
                    _roots.SelectedItem = newRoot; // triggers FillPackages()
                else
                    FillPackages();
                _packages.SelectedItem = _packages.Items.OfType<Choice>()
                    .FirstOrDefault(c => Same(c.Path, plan.TargetDirectory));
                RefreshPreview();
            }
            catch (Exception ex) when (ex is IOException ||
                ex is UnauthorizedAccessException || ex is ArgumentException ||
                ex is NotSupportedException)
            {
                VsShellUtilities.ShowMessageBox(ServiceProvider.GlobalProvider, ex.Message, "Nuovo package Java",
                    OLEMSGICON.OLEMSGICON_CRITICAL,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
        }

        private static TextBlock Label(string text, double size, bool bold)
        {
            var block = new TextBlock {
                Text = text, FontSize = size,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap
            };
            block.SetResourceReference(TextBlock.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            return block;
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

        // F2G1B-READABLE-LISTBOX: foreground esplicito sul testo delle righe.
        // Le ListBox CPS/WPF possono ignorare Foreground ereditato dalla finestra,
        // in particolare su item selezionati, hover e selezione senza focus.
        internal static ListBox MakeList(string accessibleName)
        {
            var list = new ListBox
            {
                SelectionMode = SelectionMode.Single,
                Padding = new Thickness(2),
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            Theme(list);
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            // F2G1C-UNLIMITED-PACKAGES-VIRTUALIZED
            ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
            ScrollViewer.SetCanContentScroll(list, true);
            VirtualizingStackPanel.SetIsVirtualizing(list, true);
            VirtualizingStackPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
            AutomationProperties.SetName(list, accessibleName);

            System.Drawing.Color backgroundColor;
            System.Drawing.Color foregroundColor;
            try
            {
                backgroundColor = VSColorTheme.GetThemedColor(
                    EnvironmentColors.ToolWindowBackgroundColorKey);
                foregroundColor = VSColorTheme.GetThemedColor(
                    EnvironmentColors.ToolWindowTextColorKey);
            }
            catch (Exception)
            {
                backgroundColor = System.Drawing.SystemColors.Window;
                foregroundColor = System.Drawing.SystemColors.WindowText;
            }

            bool dark = (backgroundColor.R * 299 + backgroundColor.G * 587 +
                backgroundColor.B * 114) < 128000;
            Brush normalText = new SolidColorBrush(Color.FromRgb(
                foregroundColor.R, foregroundColor.G, foregroundColor.B));
            Brush selectedBackground = new SolidColorBrush(dark
                ? Color.FromRgb(0x26, 0x4F, 0x78) : Color.FromRgb(0xC5, 0xDE, 0xF5));
            Brush hoverBackground = new SolidColorBrush(dark
                ? Color.FromRgb(0x37, 0x45, 0x55) : Color.FromRgb(0xE5, 0xF0, 0xFA));
            Brush activeText = dark ? Brushes.White :
                (Brush)new SolidColorBrush(Color.FromRgb(0x17, 0x2D, 0x42));

            // Il colore normale va specificato anche sui singoli ListBoxItem:
            // alcuni template di Visual Studio non ereditano quello della ListBox.
            list.Foreground = normalText;
            list.Resources[SystemColors.HighlightBrushKey] = selectedBackground;
            list.Resources[SystemColors.HighlightTextBrushKey] = activeText;
            list.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = selectedBackground;
            list.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = activeText;

            var style = new Style(typeof(ListBoxItem));
            style.Setters.Add(new Setter(Control.ForegroundProperty, normalText));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));

            var over = new Trigger
            {
                Property = UIElement.IsMouseOverProperty, Value = true
            };
            over.Setters.Add(new Setter(Control.BackgroundProperty, hoverBackground));
            over.Setters.Add(new Setter(Control.ForegroundProperty, activeText));
            style.Triggers.Add(over);

            var selected = new Trigger
            {
                Property = ListBoxItem.IsSelectedProperty, Value = true
            };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, selectedBackground));
            selected.Setters.Add(new Setter(Control.ForegroundProperty, activeText));
            style.Triggers.Add(selected);
            list.ItemContainerStyle = style;

            // Non usare DisplayMemberPath: il TextBlock generato automaticamente
            // puo' utilizzare colori di default diversi da quelli della ListBox.
            // Il binding alla Foreground del ListBoxItem mantiene leggibile ogni
            // riga (normale, selezionata, hover, focus e senza focus).
            var label = new FrameworkElementFactory(typeof(TextBlock));
            label.SetBinding(TextBlock.TextProperty,
                new System.Windows.Data.Binding("Label"));
            label.SetBinding(TextBlock.ForegroundProperty,
                new System.Windows.Data.Binding("Foreground")
                {
                    RelativeSource = new System.Windows.Data.RelativeSource(
                        System.Windows.Data.RelativeSourceMode.FindAncestor,
                        typeof(ListBoxItem), 1)
                });
            label.SetBinding(FrameworkElement.ToolTipProperty,
                new System.Windows.Data.Binding("Label"));
            label.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            label.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
            label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            list.ItemTemplate = new DataTemplate { VisualTree = label };
            return list;
        }


        private static bool Same(string a, string b)
        {
            return !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
                string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase);
        }

        private void FillRoots()
        {
            var roots = new List<Choice>();
            foreach (var root in JavaLanguageSupport.ProjectSystem.Packages.JavaSourceRootResolver
                .Discover(_projectDirectory, _buildHint))
            {
                if (!Directory.Exists(root.FullPath)) continue;
                string rel = root.FullPath.StartsWith(_projectDirectory + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                    ? root.FullPath.Substring(_projectDirectory.Length + 1)
                    : root.FullPath;
                roots.Add(new Choice { Path = root.FullPath, Label = rel });
            }
            _roots.ItemsSource = roots;
            JavaLanguageSupport.ProjectSystem.Packages.JavaSourceRootInfo initialRoot;
            string initialPath = JavaLanguageSupport.ProjectSystem.Packages.JavaSourceRootResolver
                .TryFindRootForPath(_projectDirectory, _initialDirectory,
                    out initialRoot, _buildHint) ? initialRoot.FullPath : null;
            _roots.SelectedItem = roots.FirstOrDefault(c => Same(c.Path, initialPath))
                ?? roots.FirstOrDefault();
            FillPackages();
        }

        private void FillPackages()
        {
            var root = _roots.SelectedItem as Choice;
            _allPackages = new List<Choice>();
            if (root == null) { _packages.ItemsSource = null; RefreshPreview(); return; }
            _allPackages.Add(new Choice { Path = root.Path, Label = "(package predefinito / radice)" });
            var pending = new Stack<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            pending.Push(root.Path);
            seen.Add(root.Path.TrimEnd('\\', '/'));
            while (pending.Count > 0)
            {
                string parent = pending.Pop();
                string[] children;
                try { children = Directory.GetDirectories(parent); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                { continue; }
                foreach (string directory in children)
                {
                    string relative = directory.Substring(root.Path.TrimEnd('\\', '/').Length)
                        .TrimStart('\\', '/');
                    string invalid;
                    if (!JavaLanguageSupport.ProjectSystem.Packages.JavaPackageResolver
                        .TryValidatePackageName(relative.Replace('\\', '.').Replace('/', '.'),
                        out invalid)) continue;
                    try
                    {
                        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                            continue;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    { continue; }
                    if (!seen.Add(directory.TrimEnd('\\', '/'))) continue;
                    _allPackages.Add(new Choice {
                        Path = directory, Label = relative.Replace('\\', '.').Replace('/', '.')
                    });
                    pending.Push(directory);
                }
            }
            _allPackages = _allPackages.Take(1).Concat(_allPackages.Skip(1)
                .OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase)).ToList();
            FilterPackages();
        }

        private void FilterPackages()
        {
            var previous = _packages.SelectedItem as Choice;
            string filter = _search.Text.Trim();
            var options = filter.Length == 0 ? _allPackages : _allPackages
                .Where(c => c.Label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            _packages.ItemsSource = options;
            _packages.SelectedItem = options.FirstOrDefault(c => previous != null && Same(c.Path, previous.Path))
                ?? options.FirstOrDefault(c => Same(c.Path, _initialDirectory))
                ?? (filter.Length == 0 ? options.FirstOrDefault() : null);
            RefreshPreview();
        }

        private void RefreshPreview()
        {
            var destination = SelectedDirectory;
            if (string.IsNullOrWhiteSpace(destination))
            {
                _packageName.Text = "Seleziona un package.";
                _destination.Text = "";
                _message.Text = "";
                _confirm.IsEnabled = false;
                return;
            }
            JavaLanguageSupport.ProjectSystem.Packages.JavaPackageInfo info;
            string error;
            if (!JavaLanguageSupport.ProjectSystem.Packages.JavaPackageResolver.TryResolveForDirectory(
                _projectDirectory, destination, out info, out error, _buildHint))
            {
                _packageName.Text = "Package non valido.";
                _destination.Text = "";
                _message.Text = error;
                _confirm.IsEnabled = false;
                return;
            }
            string typeName, fileName, nameError;
            if (!JavaTemplateWithPackageWizard.TryValidateJavaName(_javaName.Text,
                _allowSpecialDescriptors, out typeName, out fileName, out nameError))
            {
                _packageName.Text = info.Name.Length == 0 ? "(package predefinito)" : info.Name;
                _destination.Text = "";
                _message.Text = nameError;
                _confirm.IsEnabled = false;
                return;
            }
            string file = Path.Combine(destination, fileName);
            _packageName.Text = info.Name.Length == 0 ? "(package predefinito)" : info.Name;
            _destination.Text = file;
            if (File.Exists(file))
            {
                _message.Text = "Esiste già un file con questo nome. Scegli un altro package.";
                _confirm.IsEnabled = false;
            }
            else
            {
                _message.Text = "Il file sarà incluso automaticamente nel progetto Java.";
                _confirm.IsEnabled = true;
            }
        }
    }
}
