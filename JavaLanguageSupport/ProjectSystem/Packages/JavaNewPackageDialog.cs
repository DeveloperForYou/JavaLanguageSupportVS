using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace JavaLanguageSupport.ProjectSystem.Packages
{
    /// <summary>
    /// F2F.1 small modal UI; no custom editor/file templates are involved.
    /// </summary>
    internal sealed class JavaNewPackageDialog : Window
    {
        private readonly string _projectDirectory;
        private readonly string _selectedDirectory;
        private readonly bool _projectNodeSelected;
        private readonly string _buildHint;
        private readonly TextBox _name;
        private readonly TextBlock _preview;
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
            Width = 520;
            Height = 305;
            MinWidth = 480;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Background = SystemColors.WindowBrush;
            Foreground = SystemColors.WindowTextBrush;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 12;

            var content = new StackPanel { Margin = new Thickness(20) };
            content.Children.Add(new TextBlock {
                Text = "Source root: " + sourceRoot,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });
            content.Children.Add(new TextBlock {
                Text = "Package selezionato: " +
                    (string.IsNullOrEmpty(existingPackage) ? "(predefinito)" : existingPackage),
                Margin = new Thickness(0, 0, 0, 12)
            });
            content.Children.Add(new TextBlock {
                Text = "Nome nuovo package (relativo al package selezionato):",
                Margin = new Thickness(0, 0, 0, 4)
            });
            _name = new TextBox { MinHeight = 26, Margin = new Thickness(0, 0, 0, 8) };
            content.Children.Add(_name);
            _preview = new TextBlock {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            content.Children.Add(_preview);

            var actions = new StackPanel {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            _create = new Button {
                Content = "Crea", Width = 92, Height = 28,
                IsDefault = true, IsEnabled = false,
                Margin = new Thickness(0, 0, 8, 0)
            };
            var cancel = new Button {
                Content = "Annulla", Width = 92, Height = 28, IsCancel = true
            };
            _create.Click += (sender, args) => { DialogResult = true; };
            cancel.Click += (sender, args) => { DialogResult = false; };
            actions.Children.Add(_create);
            actions.Children.Add(cancel);
            content.Children.Add(actions);
            Content = content;
            _name.TextChanged += (sender, args) => RefreshPreview();
            Loaded += (sender, args) => _name.Focus();
            RefreshPreview();
        }

        private void RefreshPreview()
        {
            JavaPackageCreationPlan plan;
            string error;
            if (JavaPackageCreationService.TryPlan(_projectDirectory,
                _selectedDirectory, _projectNodeSelected, _name.Text,
                _buildHint, out plan, out error))
            {
                _preview.Text = "Package finale: " + plan.FullPackageName +
                    "\nDestinazione: " + plan.TargetDirectory;
                _create.IsEnabled = true;
            }
            else
            {
                _preview.Text = error ?? "Nome package non valido.";
                _create.IsEnabled = false;
            }
        }
    }
}
