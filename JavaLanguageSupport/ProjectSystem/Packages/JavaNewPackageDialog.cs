using System;
using System.IO;
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
    /// F2F.2: native, theme-aware Java package dialog. The service performs
    /// the same filesystem/name validation used when the command executes.
    /// This UI never writes files or modifies the CPS hierarchy itself.
    /// </summary>
    internal sealed class JavaNewPackageDialog : DialogWindow
    {
        private readonly string _projectDirectory;
        private readonly string _selectedDirectory;
        private readonly bool _projectNodeSelected;
        private readonly string _buildHint;
        private readonly TextBox _name;
        private readonly TextBlock _packagePreview;
        private readonly TextBlock _pathPreview;
        private readonly TextBlock _feedback;
        private readonly Button _create;

        public string RelativePackageName { get { return _name.Text.Trim(); } }

        public JavaNewPackageDialog(string projectDirectory, string selectedDirectory,
            bool projectNodeSelected, string buildHint,
            string sourceRoot, string existingPackage)
        {
            _projectDirectory = projectDirectory;
            _selectedDirectory = selectedDirectory;
            _projectNodeSelected = projectNodeSelected;
            _buildHint = buildHint;

            Title = "Nuovo package Java";
            Width = 640;
            Height = 460;
            MinWidth = 535;
            MinHeight = 420;
            MaxHeight = Math.Max(420, SystemParameters.WorkArea.Height - 24);
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
                Margin = new Thickness(24, 10, 24, 18)
            };
            DockPanel.SetDock(footer, Dock.Bottom);
            layout.Children.Add(footer);

            _create = new Button
            {
                Content = "Crea package", MinWidth = 126, Height = 32,
                Margin = new Thickness(0, 0, 10, 0),
                IsDefault = true, IsEnabled = false
            };
            var cancel = new Button
            {
                Content = "Annulla", MinWidth = 104, Height = 32, IsCancel = true
            };
            JavaNavigationWindowStyle.ThemeNavigationActionButton(_create);
            JavaNavigationWindowStyle.ThemeNavigationActionButton(cancel);
            _create.Click += (sender, args) =>
            {
                JavaPackageCreationPlan plan;
                string error;
                // Revalidate before closing: the folder may have appeared meanwhile.
                if (JavaPackageCreationService.TryPlan(_projectDirectory,
                    _selectedDirectory, _projectNodeSelected, _name.Text,
                    _buildHint, out plan, out error))
                    DialogResult = true;
                else
                {
                    RefreshPreview();
                    _name.Focus();
                }
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

            var title = Text("Crea un package Java", 20, true);
            title.Margin = new Thickness(0, 0, 0, 5);
            body.Children.Add(title);
            var subtitle = Text("Specifica un nome per creare la struttura di cartelle " +
                "all'interno dei sorgenti Java.", 12, false);
            subtitle.Margin = new Thickness(0, 0, 0, 20);
            body.Children.Add(subtitle);

            var context = new Border
            {
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 18)
            };
            context.SetResourceReference(Border.BorderBrushProperty,
                EnvironmentColors.ToolWindowBorderBrushKey);
            var contextBody = new StackPanel();
            context.Child = contextBody;
            contextBody.Children.Add(Caption("SOURCE ROOT"));
            contextBody.Children.Add(Detail(sourceRoot));
            var selectedLabel = Caption("PACKAGE SELEZIONATO");
            selectedLabel.Margin = new Thickness(0, 10, 0, 4);
            contextBody.Children.Add(selectedLabel);
            contextBody.Children.Add(Detail(string.IsNullOrEmpty(existingPackage)
                ? "(package predefinito)" : existingPackage));
            body.Children.Add(context);

            var label = Text("Nome del nuovo package", 13, true);
            label.Margin = new Thickness(0, 0, 0, 7);
            body.Children.Add(label);
            _name = new TextBox
            {
                MinHeight = 32, Padding = new Thickness(8, 5, 8, 5),
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 5)
            };
            _name.SetResourceReference(Control.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            _name.SetResourceReference(Control.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            _name.SetResourceReference(Control.BorderBrushProperty,
                EnvironmentColors.ToolWindowBorderBrushKey);
            AutomationProperties.SetName(_name, "Nome del nuovo package Java");
            body.Children.Add(_name);
            var hint = Text("Puoi usare nomi composti, ad esempio services.repository.", 11, false);
            hint.Margin = new Thickness(0, 0, 0, 13);
            body.Children.Add(hint);

            var preview = new Border
            {
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 10, 14, 12)
            };
            preview.SetResourceReference(Border.BorderBrushProperty,
                EnvironmentColors.ToolWindowBorderBrushKey);
            var previewBody = new StackPanel();
            preview.Child = previewBody;
            previewBody.Children.Add(Caption("ANTEPRIMA"));
            _packagePreview = Detail("—");
            _packagePreview.FontWeight = FontWeights.SemiBold;
            _packagePreview.FontSize = 14;
            previewBody.Children.Add(_packagePreview);
            _pathPreview = Detail(string.Empty);
            _pathPreview.Margin = new Thickness(0, 6, 0, 0);
            previewBody.Children.Add(_pathPreview);
            body.Children.Add(preview);
            _feedback = Text("", 12, false);
            _feedback.Margin = new Thickness(0, 9, 0, 0);
            _feedback.TextWrapping = TextWrapping.Wrap;
            body.Children.Add(_feedback);

            Content = layout;
            JavaNavigationWindowStyle.SetupWindow(this);
            Loaded += (sender, args) =>
            {
                _name.Focus();
                System.Windows.Input.Keyboard.Focus(_name);
            };
            _name.TextChanged += (sender, args) => RefreshPreview();
            RefreshPreview();
        }

        private static TextBlock Text(string value, double size, bool bold)
        {
            var text = new TextBlock
            {
                Text = value, FontSize = size,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap
            };
            text.SetResourceReference(TextBlock.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            return text;
        }

        private static TextBlock Caption(string caption)
        {
            var text = Text(caption, 11, true);
            text.Margin = new Thickness(0, 0, 0, 4);
            return text;
        }

        private static TextBlock Detail(string value)
        {
            var text = Text(value, 12, false);
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            text.ToolTip = value;
            return text;
        }

        private void RefreshPreview()
        {
            JavaPackageCreationPlan plan;
            string error;
            if (JavaPackageCreationService.TryPlan(_projectDirectory,
                _selectedDirectory, _projectNodeSelected, _name.Text,
                _buildHint, out plan, out error))
            {
                _packagePreview.Text = plan.FullPackageName;
                _packagePreview.ToolTip = plan.FullPackageName;
                _pathPreview.Text = plan.TargetDirectory;
                _pathPreview.ToolTip = plan.TargetDirectory;
                _feedback.Text = "Il package sara' creato come cartella nel progetto.";
                _create.IsEnabled = true;
            }
            else
            {
                _packagePreview.Text = "—";
                _packagePreview.ToolTip = null;
                _pathPreview.Text = string.Empty;
                _pathPreview.ToolTip = null;
                _feedback.Text = string.IsNullOrWhiteSpace(_name.Text)
                    ? "Inserisci il nome del package per vedere l'anteprima."
                    : (error ?? "Nome package Java non valido.");
                _create.IsEnabled = false;
            }
        }
    }
}
