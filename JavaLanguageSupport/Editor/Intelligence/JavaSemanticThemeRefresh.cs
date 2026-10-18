using System;
using System.ComponentModel.Composition;
using System.Windows.Media;
using System.Windows.Threading;
using JavaLanguageSupport.Editor.Intelligence;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace JavaLanguageSupport.Editor.Intelligence
{
    /// <summary>
    /// Rebinds default Java classification colors when the VS theme changes.
    /// Custom colors in Fonts and Colors are left alone if they differ from the
    /// previous palette. Only java editors, no LSP requests or buffer changes.
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("java")]
    // Also update Java classification maps in native Peek views.
    internal sealed class JavaSemanticThemeRefresh : IWpfTextViewCreationListener
    {
        [Import] internal IClassificationFormatMapService Maps = null;
        [Import] internal IClassificationTypeRegistryService Types = null;
        private static readonly object Property = new object();

        private sealed class ViewSubscription
        {
            internal IWpfTextView View;
            internal IClassificationFormatMap Map;
            internal IClassificationTypeRegistryService Types;
            internal bool LastDark;
            internal bool IsUpdating;
            internal ViewSubscription(IWpfTextView view, IClassificationFormatMap map,
                IClassificationTypeRegistryService types)
            {
                View = view; Map = map; Types = types; LastDark = JavaSemanticPalette.DarkTheme;
                VSColorTheme.ThemeChanged += OnThemeChanged;
                View.Closed += OnClosed;
                // Migrate VS-stored old default light colors without overwriting
                // custom Fonts & Colors values, even when the view starts in light mode.
                View.VisualElement.Dispatcher.BeginInvoke(new Action(Refresh),
                    DispatcherPriority.ApplicationIdle);
            }
            private void OnClosed(object sender, EventArgs args)
            {
                VSColorTheme.ThemeChanged -= OnThemeChanged;
                View.Closed -= OnClosed;
            }
            private void OnThemeChanged(ThemeChangedEventArgs args)
            {
                if (View.IsClosed) return;
                // VS may still be switching global resources at event time.
                View.VisualElement.Dispatcher.BeginInvoke(new Action(Refresh),
                    DispatcherPriority.ApplicationIdle);
            }
            private void Refresh()
            {
                if (View.IsClosed || IsUpdating) return;
                bool nowDark = JavaSemanticPalette.DarkTheme;
                // Also refresh stored legacy light defaults on first open.
                IsUpdating = true;
                try
                {
                    Map.BeginBatchUpdate();
                    try
                    {
                        foreach (var pair in Palette)
                        {
                            var classification = Types.GetClassificationType(pair.Name);
                            if (classification == null) continue;
                            var properties = Map.GetTextProperties(classification);
                            var brush = properties.ForegroundBrush as SolidColorBrush;
                            if (brush == null) continue;
                            Color activeDefault = LastDark ? pair.Dark : pair.Light;
                            Color nextDefault = nowDark ? pair.Dark : pair.Light;
                            if (brush.Color != activeDefault &&
                                !( !nowDark && (brush.Color == pair.LegacyLight ||
                                    brush.Color == pair.PreviousLight)))
                                continue; // Never override a custom Fonts & Colors choice.
                            if (brush.Color != nextDefault)
                                Map.SetTextProperties(classification,
                                    properties.SetForeground(nextDefault));
                        }
                    }
                    finally { Map.EndBatchUpdate(); }
                    LastDark = nowDark;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine("[Java theme] " + ex);
                }
                finally { IsUpdating = false; }
            }
        }
        private sealed class PaletteEntry
        {
            internal string Name;
            internal Color Dark;
            internal Color Light;
            internal Color LegacyLight;
            internal Color PreviousLight;
            internal PaletteEntry(string name, string dark, string light,
                string oldLight = null)
            {
                Name = name;
                Dark = FromHex(dark); Light = FromHex(light);
                LegacyLight = FromHex(oldLight ?? light);
                // Existing legacy palette is preserved; prior Hotfix31
                // palette is recognised separately during the transition.
                PreviousLight = FromHex(PreviousHex(name, light));
            }
            private static string PreviousHex(string name, string current)
            {
                if (name == JavaSemanticClassificationNames.Type) return "286D81";
                if (name == JavaSemanticClassificationNames.Method) return "6B4818";
                if (name == JavaSemanticClassificationNames.Variable) return "163E7A";
                if (name == JavaSemanticClassificationNames.Parameter) return "73501C";
                return current;
            }
            private static Color FromHex(string rgb)
            {
                return Color.FromRgb(Convert.ToByte(rgb.Substring(0, 2), 16),
                    Convert.ToByte(rgb.Substring(2, 2), 16),
                    Convert.ToByte(rgb.Substring(4, 2), 16));
            }
        }
        private static readonly PaletteEntry[] Palette = {
            new PaletteEntry(JavaSemanticClassificationNames.Type, "4EC9B0", "0D706B", "267F99"),
            new PaletteEntry(JavaSemanticClassificationNames.Method, "FFD166", "934712", "794F12"),
            new PaletteEntry(JavaSemanticClassificationNames.Variable, "9CDCFE", "53377E", "001080"),
            new PaletteEntry(JavaSemanticClassificationNames.Parameter, "D7BA7D", "246286", "805300"),
            new PaletteEntry(JavaSemanticClassificationNames.Annotation, "C586C0", "8325A4", "AF00DB"),
            new PaletteEntry(JavaSemanticClassificationNames.DirectiveKeyword, "C586C0", "772B97", "841FA2"),
            new PaletteEntry(JavaSemanticClassificationNames.Namespace, "82B8E6", "285F8E", "195595"),
            new PaletteEntry(JavaSemanticClassificationNames.Bracket, "DAAEF0", "6F397D", "7E3498"),
            new PaletteEntry(JavaSemanticClassificationNames.Separator, "BECDDF", "525E6B", "515D6D"),
            new PaletteEntry(JavaSemanticClassificationNames.Rainbow1, "F9D66B", "805A0A", "986800"),
            new PaletteEntry(JavaSemanticClassificationNames.Rainbow2, "CE93D8", "762C90", "8E24AA"),
            new PaletteEntry(JavaSemanticClassificationNames.Rainbow3, "6CCBFF", "165A88", "00669D"),
            new PaletteEntry(JavaSemanticClassificationNames.Rainbow4, "8FD19E", "336F35", "2E7D32"),
            new PaletteEntry(JavaSemanticClassificationNames.Rainbow5, "FF9E64", "955017", "AC4D00"),
            new PaletteEntry(JavaSemanticClassificationNames.Rainbow6, "A7A5FF", "55528B", "5553A7")
        };
        public void TextViewCreated(IWpfTextView view)
        {
            if (Maps == null || Types == null || view == null || view.IsClosed ||
                !view.TextBuffer.ContentType.IsOfType("java")) return;
            view.Properties.GetOrCreateSingletonProperty(Property, () =>
                new ViewSubscription(view, Maps.GetClassificationFormatMap(view), Types));
        }
    }
}
