using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using JavaLanguageSupport.Editor.Navigation;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace JavaLanguageSupport.Editor.Refactoring
{
    /// <summary>
    /// Shared generation-dialog UI. WPF tables scroll and virtualize inside the
    /// remaining client area; the button strip is never part of the scroller.
    /// Native caption, Minimize button and ownership are handled independently
    /// from WPF brushes. This class does not modify any Java code.
    /// </summary>
    internal static class JavaGenerationDialogStyle
    {
        internal static TextBlock Text(string label, bool bold = false)
        {
            var text = new TextBlock
            {
                Text = label, TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 8)
            };
            text.SetResourceReference(TextBlock.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            return text;
        }

        internal static DataGrid Table()
        {
            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserReorderColumns = false,
                CanUserSortColumns = false,
                CanUserResizeRows = false,
                CanUserResizeColumns = true,
                SelectionMode = DataGridSelectionMode.Single,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.All,
                EnableRowVirtualization = true,
                EnableColumnVirtualization = true,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                RowHeight = 28,
                ColumnHeaderHeight = 30,
                MinHeight = 0,
                MinWidth = 0
            };
            grid.SetResourceReference(Control.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            grid.SetResourceReference(Control.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            grid.SetResourceReference(Control.BorderBrushProperty,
                EnvironmentColors.ToolWindowBorderBrushKey);
            grid.SetResourceReference(DataGrid.VerticalGridLinesBrushProperty,
                EnvironmentColors.ToolWindowBorderBrushKey);
            grid.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty,
                EnvironmentColors.ToolWindowBorderBrushKey);
            // D1: keep selected cells readable even after the DataGrid loses focus.
            // Constrain the override to the generation table, not the whole IDE.
            bool darkSelection = JavaLanguageSupport.Editor.Intelligence.JavaSemanticPalette.DarkTheme;
            Brush selectedBackground = new SolidColorBrush(darkSelection
                ? Color.FromRgb(0x26, 0x4F, 0x78) : Color.FromRgb(0xC5, 0xDE, 0xF5));
            Brush selectedText = darkSelection ? Brushes.White :
                (Brush)new SolidColorBrush(Color.FromRgb(0x17, 0x2D, 0x42));
            grid.Resources[SystemColors.HighlightBrushKey] = selectedBackground;
            grid.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = selectedBackground;
            grid.Resources[SystemColors.HighlightTextBrushKey] = selectedText;
            grid.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = selectedText;
            VirtualizingStackPanel.SetIsVirtualizing(grid, true);
            VirtualizingStackPanel.SetVirtualizationMode(grid, VirtualizationMode.Recycling);
            ScrollViewer.SetCanContentScroll(grid, true);
            grid.LoadingRow += (sender, args) =>
            {
                args.Row.SetResourceReference(Control.ForegroundProperty,
                    EnvironmentColors.ToolWindowTextBrushKey);
                args.Row.SetResourceReference(Control.BackgroundProperty,
                    EnvironmentColors.ToolWindowBackgroundBrushKey);
            };
            // Give headers a readable VS palette, not the OS-default WPF theme.
            grid.Loaded += (sender, args) => ThemeHeader(grid);
            return grid;
        }

        private static void ThemeHeader(DataGrid grid)
        {
            try
            {
                var background = VSColorTheme.GetThemedColor(
                    EnvironmentColors.ToolWindowBackgroundColorKey);
                var foreground = VSColorTheme.GetThemedColor(
                    EnvironmentColors.ToolWindowTextColorKey);
                var style = new Style(typeof(DataGridColumnHeader));
                style.Setters.Add(new Setter(Control.BackgroundProperty,
                    new SolidColorBrush(Color.FromRgb(background.R, background.G, background.B))));
                style.Setters.Add(new Setter(Control.ForegroundProperty,
                    new SolidColorBrush(Color.FromRgb(foreground.R, foreground.G, foreground.B))));
                style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7, 4, 7, 4)));
                grid.ColumnHeaderStyle = style;
            }
            catch (Exception) { /* The default DataGrid template is a fallback. */ }
        }

        internal static void Column(DataGrid grid, string title, string property, double weight)
        {
            var label = new FrameworkElementFactory(typeof(TextBlock));
            label.SetBinding(TextBlock.TextProperty, new Binding(property));
            label.SetBinding(FrameworkElement.ToolTipProperty, new Binding(property));
            label.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            label.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
            label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            label.SetValue(FrameworkElement.MarginProperty, new Thickness(7, 0, 7, 0));
            var template = new DataTemplate { VisualTree = label };
            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = title,
                CellTemplate = template,
                IsReadOnly = true,
                Width = new DataGridLength(weight, DataGridLengthUnitType.Star),
                MinWidth = 90
            });
        }

        internal static void CheckColumn(DataGrid grid, string title, string property,
            string availableProperty)
        {
            var check = new FrameworkElementFactory(typeof(CheckBox));
            check.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            check.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            check.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 2, 6, 2));
            check.SetBinding(CheckBox.IsCheckedProperty, new Binding(property)
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
            check.SetBinding(UIElement.IsEnabledProperty, new Binding(availableProperty));
            var template = new DataTemplate { VisualTree = check };
            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = title,
                CellTemplate = template,
                Width = new DataGridLength(100),
                MinWidth = 82
            });
        }

        internal static bool Show(string title, UIElement content, double width = 790,
            double height = 580, Func<bool> canGenerate = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var desktop = SystemParameters.WorkArea;
            var maxWidth = Math.Max(440, desktop.Width - 40);
            var maxHeight = Math.Max(370, desktop.Height - 40);
            var window = new Window
            {
                Title = title,
                Width = Math.Min(width, maxWidth),
                Height = Math.Min(height, maxHeight),
                MinWidth = Math.Min(590, maxWidth),
                MinHeight = Math.Min(350, maxHeight),
                // Let Windows handle maximizing on the active monitor.
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.CanResize,
                ShowInTaskbar = false,
                ShowActivated = true
            };
            Configure(window);
            var root = new DockPanel { LastChildFill = true };
            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(8, 9, 11, 10)
            };
            var ok = Button("Genera", true, false);
            var cancel = Button("Annulla", false, true);
            ok.Click += (sender, args) => { if (canGenerate == null || canGenerate()) window.DialogResult = true; };
            cancel.Click += (sender, args) => { window.DialogResult = false; };
            footer.Children.Add(ok);
            footer.Children.Add(cancel);
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);
            root.Children.Add(content);
            window.Content = root;
            return window.ShowDialog() == true;
        }

        // D1: WPF's stock and VS-themed Button templates may reintroduce
        // black text over dark backgrounds during hover or keyboard focus.
        // A local template retains VS dynamic resource brushes while providing
        // contrast-safe hover, focus and pressed states for the dialog footer.
        private static void ThemeGenerationButton(Button button)
        {
            bool dark = JavaLanguageSupport.Editor.Intelligence.JavaSemanticPalette.DarkTheme;
            Brush hover = new SolidColorBrush(dark
                ? Color.FromRgb(0x37, 0x45, 0x55) : Color.FromRgb(0xD8, 0xE9, 0xF7));
            Brush pressed = new SolidColorBrush(dark
                ? Color.FromRgb(0x28, 0x4A, 0x66) : Color.FromRgb(0xB9, 0xD8, 0xEF));
            Brush contrast = dark ? Brushes.White :
                (Brush)new SolidColorBrush(Color.FromRgb(0x17, 0x2D, 0x42));
            var template = new ControlTemplate(typeof(System.Windows.Controls.Button));
            var frame = new FrameworkElementFactory(typeof(Border));
            frame.Name = "GenerationButtonFrame";
            frame.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
            frame.SetValue(Border.PaddingProperty, new Thickness(12, 5, 12, 5));
            frame.SetBinding(Border.BackgroundProperty, new Binding("Background") {
                RelativeSource = RelativeSource.TemplatedParent });
            frame.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") {
                RelativeSource = RelativeSource.TemplatedParent });
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.Name = "GenerationButtonContent";
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty,
                HorizontalAlignment.Center);
            content.SetValue(FrameworkElement.VerticalAlignmentProperty,
                VerticalAlignment.Center);
            content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            content.SetBinding(TextElement.ForegroundProperty, new Binding("Foreground") {
                RelativeSource = RelativeSource.TemplatedParent });
            frame.AppendChild(content);
            template.VisualTree = frame;
            var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            over.Setters.Add(new Setter(Border.BackgroundProperty, hover, "GenerationButtonFrame"));
            over.Setters.Add(new Setter(TextElement.ForegroundProperty, contrast,
                "GenerationButtonContent"));
            template.Triggers.Add(over);
            var focus = new Trigger {
                Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
            focus.Setters.Add(new Setter(Border.BackgroundProperty, hover, "GenerationButtonFrame"));
            focus.Setters.Add(new Setter(TextElement.ForegroundProperty, contrast,
                "GenerationButtonContent"));
            template.Triggers.Add(focus);
            var down = new Trigger { Property = System.Windows.Controls.Button.IsPressedProperty, Value = true };
            down.Setters.Add(new Setter(Border.BackgroundProperty, pressed, "GenerationButtonFrame"));
            down.Setters.Add(new Setter(TextElement.ForegroundProperty, contrast,
                "GenerationButtonContent"));
            template.Triggers.Add(down);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.48));
            template.Triggers.Add(disabled);
            button.Template = template;
        }

        private static Button Button(string label, bool isDefault, bool isCancel)
        {
            var button = new Button { Content = label, MinWidth = 106,
                IsDefault = isDefault, IsCancel = isCancel,
                Margin = new Thickness(5, 0, 0, 0), Padding = new Thickness(8, 4, 8, 4) };
            button.SetResourceReference(FrameworkElement.StyleProperty,
                Microsoft.VisualStudio.Shell.VsResourceKeys.ThemedDialogButtonStyleKey);
            button.SetResourceReference(Control.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            button.SetResourceReference(Control.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            button.SetResourceReference(Control.BorderBrushProperty,
                EnvironmentColors.ToolWindowBorderBrushKey);
            ThemeGenerationButton(button);
            return button;
        }

        internal static void Configure(Window window)
        {
            // Shared Java dialog policy: white OS caption in every VS theme,
            // dark/light VS palette in the client area, no Minimize.
            JavaNavigationWindowStyle.SetupWindow(window);
        }
    }
}
