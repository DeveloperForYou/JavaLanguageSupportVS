using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using EnvDTE;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace JavaLanguageSupport.Editor.Navigation
{
    /// <summary>
    /// Shared, focus-independent appearance for Java navigation and references.
    /// All text brushes follow the current Visual Studio theme dynamically.
    /// Selection is an accent BLUE UNDERLINE, never the OS focus-dependent
    /// selected-background brush (which made text invisible in dark themes).
    /// </summary>
    internal static class JavaNavigationWindowStyle
    {
        private static readonly Brush AccentBlue = new SolidColorBrush(Color.FromRgb(0, 122, 204));

        private const int GwlStyle = -16;
        private const int WsMinimizeBox = 0x00020000;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpFrameChanged = 0x0020;

        // Every titled Java dialog uses an ALWAYS-WHITE native caption, while its
        // client remains Visual Studio themed (dark/light). DWM attributes 35/36
        // are Windows 11-specific; gracefully leave system styling on older OSes.
        private const uint ScClose = 0xF060;
        private const uint MfByCommand = 0x00000000;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute,
            ref int value, int size);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetSystemMenu(IntPtr hwnd, bool revert);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DeleteMenu(IntPtr menu, uint position, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DrawMenuBar(IntPtr hwnd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint flags);

        internal static void SetupWindow(System.Windows.Window window)
        {
            window.SetResourceReference(Control.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            window.SetResourceReference(Control.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            window.SourceInitialized += (sender, args) =>
            {
                RemoveMinimizeButton(window);
                WhiteCaption(window);
                DisableSystemClose(window);
            };
            // Keep the caption light even when activating or deactivating VS.
            window.Activated += (sender, args) => WhiteCaption(window);

            var dte = Package.GetGlobalService(typeof(SDTE)) as DTE;
            if (dte != null && dte.MainWindow != null)
            {
                // Visual Studio 2026 DTE.MainWindow.HWnd is already IntPtr.
                new WindowInteropHelper(window).Owner = dte.MainWindow.HWnd;
            }
        }

        private static void WhiteCaption(System.Windows.Window window)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;
                int light = 0;
                if (DwmSetWindowAttribute(hwnd, 20, ref light, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref light, sizeof(int));
                int white = 0x00FFFFFF; // COLORREF, RGB(255,255,255)
                int black = 0x00000000;
                DwmSetWindowAttribute(hwnd, 35, ref white, sizeof(int));
                DwmSetWindowAttribute(hwnd, 36, ref black, sizeof(int));
            }
            catch (Exception) { /* Optional OS theme capability. */ }
        }

        private static void DisableSystemClose(System.Windows.Window window)
        {
            // Win32 cannot reliably HIDE only X while retaining the stock
            // Maximize/Restore and resize frame. Disable it via the system menu;
            // all these dialogs supply an explicit bottom action/cancel button.
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;
                IntPtr menu = GetSystemMenu(hwnd, false);
                if (menu != IntPtr.Zero && DeleteMenu(menu, ScClose, MfByCommand))
                    DrawMenuBar(hwnd);
            }
            catch (Exception) { /* Leave X as system fallback. */ }
        }

        private static void RemoveMinimizeButton(System.Windows.Window window)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int style = GetWindowLong(hwnd, GwlStyle);
            if ((style & WsMinimizeBox) == 0) return;
            SetWindowLong(hwnd, GwlStyle, style & ~WsMinimizeBox);
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged);
        }

        internal static void ThemeText(TextBlock text)
        {
            text.SetResourceReference(TextBlock.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
        }

        internal static void ThemeControl(Control control)
        {
            control.SetResourceReference(Control.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            control.SetResourceReference(Control.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
        }

        internal static Border TreeHeader(string label, bool bold)
        {
            var text = new TextBlock {
                Text = label,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
            };
            ThemeText(text);
            return new Border {
                Child = text,
                Padding = new Thickness(2, 2, 2, 3),
                BorderThickness = new Thickness(0),
                BorderBrush = AccentBlue,
                Background = Brushes.Transparent
            };
        }

        private static void KeepSelectionColors(ItemsControl control)
        {
            // Standard WPF TreeView/ListBox templates switch selection brushes
            // on focus changes. Keep the native selection background transparent
            // and draw an explicit underline that never changes with focus.
            control.Resources[SystemColors.HighlightBrushKey] = Brushes.Transparent;
            control.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = Brushes.Transparent;
        }

        internal static void SetupTree(TreeView tree)
        {
            ThemeControl(tree);
            KeepSelectionColors(tree);
            var style = new Style(typeof(TreeViewItem));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            tree.ItemContainerStyle = style;
            tree.SelectedItemChanged += (sender, e) =>
            {
                SetTreeUnderline(e.OldValue as TreeViewItem, false);
                SetTreeUnderline(e.NewValue as TreeViewItem, true);
            };
        }

        private static void SetTreeUnderline(TreeViewItem item, bool active)
        {
            Border header = item == null ? null : item.Header as Border;
            if (header != null)
                header.BorderThickness = active ? new Thickness(0, 0, 0, 2) : new Thickness(0);
        }

        internal static void SetupList(ListBox list)
        {
            ThemeControl(list);
            KeepSelectionColors(list);
            var style = new Style(typeof(ListBoxItem));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            var selected = new Trigger {
                Property = ListBoxItem.IsSelectedProperty, Value = true
            };
            selected.Setters.Add(new Setter(Control.BorderBrushProperty, AccentBlue));
            selected.Setters.Add(new Setter(Control.BorderThicknessProperty,
                new Thickness(0, 0, 0, 2)));
            selected.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Triggers.Add(selected);
            list.ItemContainerStyle = style;
            // A Border inside the item is not affected by VS/OS focus visuals.
            // This guarantees the selected item has a BLUE underline whether
            // the ListBox, code editor or another window currently has focus.
            list.SelectionChanged += (sender, args) =>
            {
                foreach (object oldItem in args.RemovedItems)
                    SetListUnderline(oldItem as ListBoxItem, false);
                foreach (object newItem in args.AddedItems)
                    SetListUnderline(newItem as ListBoxItem, true);
            };
        }

        internal static Border ListHeader(string label)
        {
            var text = new TextBlock { Text = label,
                TextTrimming = TextTrimming.CharacterEllipsis };
            ThemeText(text);
            return new Border { Child = text, Background = Brushes.Transparent,
                BorderBrush = AccentBlue, BorderThickness = new Thickness(0),
                Padding = new Thickness(2, 1, 2, 2) };
        }

        private static void SetListUnderline(ListBoxItem item, bool selected)
        {
            var border = item == null ? null : item.Content as Border;
            if (border != null)
                border.BorderThickness = selected ? new Thickness(0, 0, 0, 2) : new Thickness(0);
        }
    }
}
