using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using JavaLanguageSupport.Editor.Navigation;
using JavaLanguageSupport.ProjectSystem.Packages;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace JavaLanguageSupport.ProjectSystem.Templates.Wizards
{
    public enum JavaSpecialItemKind { Module, ResourceFolder }

    /// <summary>
    /// Select the Java 9+ module source root, or a Maven/Gradle resource directory.
    /// This dialog does not write files or folders.
    /// </summary>
    internal sealed class JavaSpecialItemDialog : DialogWindow
    {
        private sealed class Location
        {
            public string Path { get; set; }
            public string Label { get; set; }
            public override string ToString() { return Label; }
        }

        private readonly string _projectDirectory;
        private readonly JavaSpecialItemKind _kind;
        private readonly ListBox _locations;
        private readonly TextBox _moduleName;
        private readonly CheckBox _openModule;
        private readonly TextBlock _preview;
        private readonly TextBlock _status;
        private readonly Button _confirm;

        internal string SelectedDirectory
        {
            get
            {
                var item = _locations.SelectedItem as Location;
                return item == null ? null : item.Path;
            }
        }
        internal string ModuleName { get { return _moduleName == null ? null : _moduleName.Text.Trim(); } }
        internal bool IsOpenModule { get { return _openModule != null && _openModule.IsChecked == true; } }

        internal JavaSpecialItemDialog(string projectDirectory, string selectedDirectory,
            string buildHint, JavaSpecialItemKind kind)
        {
            _projectDirectory = Path.GetFullPath(projectDirectory);
            _kind = kind;
            Title = kind == JavaSpecialItemKind.Module ? "Nuovo modulo Java (JDK 9+)" :
                "Nuova cartella risorse Java";
            Width = 620;
            Height = kind == JavaSpecialItemKind.Module ? 435 : 355;
            MinWidth = 520;
            MinHeight = 300;
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 12;
            SetResourceReference(BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            SetResourceReference(ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);

            var panel = new DockPanel { LastChildFill = true };
            Content = panel;
            var footer = new StackPanel {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(18, 8, 18, 12)
            };
            DockPanel.SetDock(footer, Dock.Bottom);
            panel.Children.Add(footer);
            _confirm = new Button {
                Content = kind == JavaSpecialItemKind.Module ? "Crea modulo" : "Crea cartella",
                Height = 30, MinWidth = 126, Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true, IsEnabled = false
            };
            var cancel = new Button { Content = "Annulla", Height = 30,
                MinWidth = 100, IsCancel = true };
            JavaNavigationWindowStyle.ThemeNavigationActionButton(_confirm);
            JavaNavigationWindowStyle.ThemeNavigationActionButton(cancel);
            _confirm.Click += (sender, args) => { if (_confirm.IsEnabled) DialogResult = true; };
            cancel.Click += (sender, args) => DialogResult = false;
            footer.Children.Add(_confirm);
            footer.Children.Add(cancel);

            var layout = new Grid { Margin = new Thickness(20, 17, 20, 6) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.Children.Add(layout);

            var title = NewText(kind == JavaSpecialItemKind.Module ?
                "Crea module-info.java" : "Crea una cartella di risorse", 17, true);
            title.Margin = new Thickness(0, 0, 0, 12);
            Grid.SetRow(title, 0);
            layout.Children.Add(title);
            _locations = JavaTemplatePackageSelectDialog.MakeList(
                kind == JavaSpecialItemKind.Module ? "Source root del modulo" :
                "Destinazione delle risorse");
            Grid.SetRow(_locations, 1);
            layout.Children.Add(_locations);

            var inputs = new StackPanel { Margin = new Thickness(0, 10, 0, 6) };
            Grid.SetRow(inputs, 2);
            layout.Children.Add(inputs);
            if (kind == JavaSpecialItemKind.Module)
            {
                inputs.Children.Add(NewText("NOME DEL MODULO", 11, true));
                _moduleName = new TextBox {
                    Height = 30, Margin = new Thickness(0, 5, 0, 8),
                    Padding = new Thickness(8, 4, 8, 4),
                    ToolTip = "Ad esempio: com.example.app"
                };
                Theme(_moduleName);
                AutomationProperties.SetName(_moduleName, "Nome del modulo Java");
                inputs.Children.Add(_moduleName);
                _openModule = new CheckBox {
                    Content = "Modulo aperto (open module)", Margin = new Thickness(0, 0, 0, 2)
                };
                _openModule.SetResourceReference(Control.ForegroundProperty,
                    EnvironmentColors.ToolWindowTextBrushKey);
                inputs.Children.Add(_openModule);
                inputs.Children.Add(NewText("Richiede JDK 9 o successivo.", 11, false));
                _moduleName.TextChanged += (sender, args) => Refresh();
                _openModule.Checked += (sender, args) => Refresh();
                _openModule.Unchecked += (sender, args) => Refresh();
            }
            else
            {
                inputs.Children.Add(NewText(
                    "Le directory resources non sono package Java.", 11, false));
            }

            var preview = new Border { Margin = new Thickness(0, 8, 0, 0),
                Padding = new Thickness(11, 7, 11, 8), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3) };
            preview.SetResourceReference(Border.BorderBrushProperty,
                EnvironmentColors.ToolWindowBorderBrushKey);
            Grid.SetRow(preview, 3);
            layout.Children.Add(preview);
            var stack = new StackPanel();
            preview.Child = stack;
            _preview = NewText("", 11, true);
            _status = NewText("", 11, false);
            _status.Margin = new Thickness(0, 5, 0, 0);
            stack.Children.Add(_preview);
            stack.Children.Add(_status);
            JavaNavigationWindowStyle.SetupWindow(this);

            var destinations = new List<Location>();
            if (kind == JavaSpecialItemKind.Module)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var root in JavaSourceRootResolver.Discover(_projectDirectory, buildHint))
                {
                    // Conventional source roots may not exist yet; creation of
                    // module-info.java will create the selected root if needed.
                    if (root.Kind == JavaSourceRootKind.Test)
                        continue;
                    if (!seen.Add(root.FullPath.TrimEnd('\\', '/'))) continue;
                    destinations.Add(new Location { Path = root.FullPath,
                        Label = Relative(root.FullPath) });
                }
            }
            else
            {
                destinations.Add(new Location {
                    Path = Path.Combine(_projectDirectory, "src", "main", "resources"),
                    Label = "src\\main\\resources (principale)" });
                destinations.Add(new Location {
                    Path = Path.Combine(_projectDirectory, "src", "test", "resources"),
                    Label = "src\\test\\resources (test)" });
            }
            _locations.ItemsSource = destinations;
            var initial = destinations.FirstOrDefault(x => IsSameOrDescendant(x.Path, selectedDirectory));
            if (initial == null && kind == JavaSpecialItemKind.ResourceFolder &&
                IsSameOrDescendant(Path.Combine(_projectDirectory, "src", "test"), selectedDirectory))
                initial = destinations.LastOrDefault();
            _locations.SelectedItem = initial ?? destinations.FirstOrDefault();
            _locations.SelectionChanged += (sender, args) => Refresh();
            Refresh();
            Loaded += (sender, args) => {
                if (_moduleName != null) _moduleName.Focus();
                else _locations.Focus();
            };
        }

        private string Relative(string full)
        {
            string prefix = _projectDirectory.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ?
                full.Substring(prefix.Length) : full;
        }

        private static bool IsSameOrDescendant(string parent, string child)
        {
            if (string.IsNullOrWhiteSpace(child)) return false;
            string root = parent.TrimEnd('\\', '/');
            string path = child.TrimEnd('\\', '/');
            return string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private void Refresh()
        {
            string directory = SelectedDirectory;
            if (string.IsNullOrWhiteSpace(directory))
            {
                _preview.Text = "Seleziona una destinazione.";
                _status.Text = "";
                _confirm.IsEnabled = false;
                return;
            }
            if (_kind == JavaSpecialItemKind.ResourceFolder)
            {
                _preview.Text = directory;
                _status.Text = Directory.Exists(directory) ?
                    "La directory esiste: sarà registrata nel progetto se necessario." :
                    "Verrà creata e registrata come cartella nel .javaproj.";
                _confirm.IsEnabled = true;
                return;
            }
            _preview.Text = Path.Combine(directory, "module-info.java");
            if (File.Exists(Path.Combine(directory, "module-info.java")))
            {
                _status.Text = "module-info.java esiste già in questa source root.";
                _confirm.IsEnabled = false;
                return;
            }
            string invalid;
            if (string.IsNullOrWhiteSpace(ModuleName) ||
                !JavaPackageResolver.TryValidatePackageName(ModuleName, out invalid))
            {
                _status.Text = "Inserisci un nome di modulo valido (es. com.example.app).";
                _confirm.IsEnabled = false;
                return;
            }
            _status.Text = (IsOpenModule ? "open module " : "module ") + ModuleName + " { ... }";
            _confirm.IsEnabled = true;
        }

        private static TextBlock NewText(string value, double size, bool bold)
        {
            var text = new TextBlock { Text = value, FontSize = size,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
            text.SetResourceReference(TextBlock.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            return text;
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
    }
}
