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
    // The SDK requires a role for IWpfTextViewCreationListener exports.
    // The semantic tagger also calls AttachView for Java Peek/non-Document roles.
    [TextViewRole(PredefinedTextViewRoles.Document)]
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
                // VS often applies its own editor-format map *after* the
                // notification. Recheck once, never spin or query JDT LS.
                System.Threading.Tasks.Task.Delay(650).ContinueWith(t =>
                {
                    try
                    {
                        View.VisualElement.Dispatcher.BeginInvoke(new Action(Refresh),
                            DispatcherPriority.ApplicationIdle);
                    }
                    catch (InvalidOperationException) { /* VS view shutting down */ }
                });
            }
            private void Refresh()
            {
                if (View.IsClosed || IsUpdating || System.Windows.SystemParameters.HighContrast) return;
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
                            Color nextDefault = nowDark ? pair.Dark : pair.Light;
                            // Theme switches can restore old colors *after* the
                            // ThemeChanged event. Accept only shipped defaults,
                            // never arbitrary user-defined Fonts & Colors values.
                            Color current = brush.Color;
                            bool isBundledDefault = current == pair.Dark ||
                                current == pair.Light || current == pair.LegacyLight ||
                                current == pair.PreviousLight ||
                                current == pair.PreviousCurrentLight ||
                                current == pair.LastKnownLight ||
                                current == pair.AlternateKnownLight ||
                                JavaSemanticThemeRefresh.IsHotfix35Preset(pair.Name, current) ||
                                JavaSemanticThemeRefresh.IsHotfix37Preset(pair.Name, current);
                            if (!isBundledDefault) continue;
                            var nextProperties = brush.Color == nextDefault
                                ? properties : properties.SetForeground(nextDefault);
                            bool? boldPreset = JavaSemanticThemeRefresh.DesiredBold(pair.Name, nowDark);
                            if (boldPreset.HasValue &&
                                (properties.BoldEmpty || properties.Bold != boldPreset.Value))
                                nextProperties = nextProperties.SetBold(boldPreset.Value);
                            if (!ReferenceEquals(nextProperties, properties))
                                Map.SetTextProperties(classification, nextProperties);
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
        private static bool IsHotfix35Preset(string name, Color current)
        {
            if (name == JavaSemanticClassificationNames.Type)
                return current == Rgb("0B665F");
            if (name == JavaSemanticClassificationNames.Method)
                return current == Rgb("FFD166") || current == Rgb("8B421B");
            if (name == JavaSemanticClassificationNames.Variable)
                return current == Rgb("2D4E85");
            if (name == JavaSemanticClassificationNames.Parameter)
                return current == Rgb("703F91");
            if (name == JavaSemanticClassificationNames.Annotation)
                return current == Rgb("973161");
            if (name == JavaSemanticClassificationNames.DirectiveKeyword)
                return current == Rgb("7A2F81");
            if (name == JavaSemanticClassificationNames.Namespace)
                return current == Rgb("355A83");
            if (name == JavaSemanticClassificationNames.Bracket)
                return current == Rgb("5D477D");
            if (name == JavaSemanticClassificationNames.Separator)
                return current == Rgb("BECDDF") || current == Rgb("4C5663");
            return false;
        }
        private static Color Rgb(string rgb)
        {
            return Color.FromRgb(Convert.ToByte(rgb.Substring(0, 2), 16),
                Convert.ToByte(rgb.Substring(2, 2), 16),
                Convert.ToByte(rgb.Substring(4, 2), 16));
        }

        private static bool? DesiredBold(string name, bool isDark)
        {
            if (name == JavaSemanticClassificationNames.Type)
                return !isDark;
            if (name == JavaSemanticClassificationNames.Annotation ||
                name == JavaSemanticClassificationNames.Namespace ||
                name == JavaSemanticClassificationNames.DirectiveKeyword)
                return true;
            return null;
        }

        private static bool IsHotfix37Preset(string name, Color color)
        {
            if (name == JavaSemanticClassificationNames.Namespace) return color == Rgb("0035C5");
            if (name == JavaSemanticClassificationNames.DirectiveKeyword) return color == Rgb("BC06AE") || color == Rgb("C586C0");
            if (name == JavaSemanticClassificationNames.Variable) return color == Rgb("01423E");
            if (name == JavaKeywordClassificationNames.Keyword) return color == Rgb("0039B3");
            if (name == JavaKeywordClassificationNames.Primitive) return color == Rgb("086B7A") || color == Rgb("4EC9B0");
            if (name == JavaKeywordClassificationNames.Var) return color == Rgb("974B00") || color == Rgb("D9BAF5");
            if (name == JavaKeywordClassificationNames.Modifier) return color == Rgb("8A2A7A") || color == Rgb("C586C0");
            if (name == JavaKeywordClassificationNames.Constant) return color == Rgb("993313") || color == Rgb("B5CEA8");
            return false;
        }

        private sealed class PaletteEntry
        {
            internal string Name;
            internal Color Dark;
            internal Color Light;
            internal Color LegacyLight;
            internal Color PreviousLight;
            internal Color AlternateKnownLight;
            internal Color LastKnownLight;
            internal Color PreviousCurrentLight;
            internal PaletteEntry(string name, string dark, string light,
                string oldLight = null)
            {
                Name = name;
                Dark = FromHex(dark); Light = FromHex(light);
                LegacyLight = FromHex(oldLight ?? light);
                // Existing legacy palette is preserved; prior Hotfix31
                // palette is recognised separately during the transition.
                PreviousLight = FromHex(PreviousHex(name, light));
                AlternateKnownLight = FromHex(AlternateLightHex(name, light));
                LastKnownLight = FromHex(KnownLightHex(name, light));
                PreviousCurrentLight = FromHex(PreviousHotfix32(name, light));
            }
            // Extra migration step: VS may cache the immediately preceding
            // Hotfix32 default even after the editor changes theme.
            private static string PreviousHotfix32(string name, string current)
            {
                if (name == JavaSemanticClassificationNames.Type) return "0D706B";
                if (name == JavaSemanticClassificationNames.Method) return "934712";
                if (name == JavaSemanticClassificationNames.Variable) return "53377E";
                if (name == JavaSemanticClassificationNames.Parameter) return "246286";
                if (name == JavaSemanticClassificationNames.Annotation) return "8325A4";
                return current;
            }
            private static string KnownLightHex(string name, string current)
            {
                if (name == JavaSemanticClassificationNames.Type) return "0D706B";
                if (name == JavaSemanticClassificationNames.Method) return "934712";
                if (name == JavaSemanticClassificationNames.Variable) return "53377E";
                if (name == JavaSemanticClassificationNames.Parameter) return "246286";
                if (name == JavaSemanticClassificationNames.Annotation) return "8325A4";
                if (name == JavaSemanticClassificationNames.DirectiveKeyword) return "772B97";
                if (name == JavaSemanticClassificationNames.Namespace) return "285F8E";
                if (name == JavaSemanticClassificationNames.Bracket) return "6F397D";
                if (name == JavaSemanticClassificationNames.Separator) return "525E6B";
                if (name == JavaSemanticClassificationNames.Rainbow1) return "805A0A";
                if (name == JavaSemanticClassificationNames.Rainbow2) return "762C90";
                if (name == JavaSemanticClassificationNames.Rainbow3) return "165A88";
                if (name == JavaSemanticClassificationNames.Rainbow4) return "336F35";
                if (name == JavaSemanticClassificationNames.Rainbow5) return "955017";
                if (name == JavaSemanticClassificationNames.Rainbow6) return "55528B";
                return current;
            }

            private static string AlternateLightHex(string name, string current)
            {
                if (name == JavaSemanticClassificationNames.Type) return "006D55";
                if (name == JavaSemanticClassificationNames.Method) return "A24B00";
                if (name == JavaSemanticClassificationNames.Variable) return "263A9A";
                if (name == JavaSemanticClassificationNames.Parameter) return "743B82";
                if (name == JavaSemanticClassificationNames.Annotation) return "952D58";
                if (name == JavaSemanticClassificationNames.DirectiveKeyword) return "772B97";
                if (name == JavaSemanticClassificationNames.Namespace) return "285F8E";
                if (name == JavaSemanticClassificationNames.Bracket) return "6F397D";
                if (name == JavaSemanticClassificationNames.Separator) return "525E6B";
                if (name == JavaSemanticClassificationNames.Rainbow1) return "805A0A";
                if (name == JavaSemanticClassificationNames.Rainbow2) return "762C90";
                if (name == JavaSemanticClassificationNames.Rainbow3) return "165A88";
                if (name == JavaSemanticClassificationNames.Rainbow4) return "336F35";
                if (name == JavaSemanticClassificationNames.Rainbow5) return "955017";
                if (name == JavaSemanticClassificationNames.Rainbow6) return "55528B";
                return current;
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
            new PaletteEntry(JavaKeywordClassificationNames.Keyword, "569CD6", "0000FF"),
            new PaletteEntry(JavaKeywordClassificationNames.Primitive, "569CD6", "0000FF"),
            new PaletteEntry(JavaKeywordClassificationNames.Var, "569CD6", "0000FF"),
            new PaletteEntry(JavaKeywordClassificationNames.Modifier, "569CD6", "0000FF"),
            new PaletteEntry(JavaKeywordClassificationNames.Constant, "569CD6", "0000FF"),
            new PaletteEntry(JavaSemanticClassificationNames.Type, "4EC9B0", "2B91AF", "267F99"),
            new PaletteEntry(JavaSemanticClassificationNames.Method, "DCDCAA", "74531F", "794F12"),
            new PaletteEntry(JavaSemanticClassificationNames.Variable, "9CDCFE", "000000", "001080"),
            new PaletteEntry(JavaSemanticClassificationNames.Parameter, "D7BA7D", "765C24", "805300"),
            new PaletteEntry(JavaSemanticClassificationNames.Annotation, "C586C0", "7E7E7E", "AF00DB"),
            new PaletteEntry(JavaSemanticClassificationNames.DirectiveKeyword, "569CD6", "7F0055", "841FA2"),
            new PaletteEntry(JavaSemanticClassificationNames.Namespace, "82B8E6", "0000FF", "195595"),
            new PaletteEntry(JavaSemanticClassificationNames.Bracket, "DAAEF0", "000000", "7E3498"),
            new PaletteEntry(JavaSemanticClassificationNames.Separator, "FFFFFF", "000000", "515D6D"),
            new PaletteEntry(JavaSemanticClassificationNames.Rainbow1, "F9D66B", "80530B", "986800"),
            new PaletteEntry(JavaSemanticClassificationNames.Rainbow2, "CE93D8", "6F2F8D", "8E24AA"),
            new PaletteEntry(JavaSemanticClassificationNames.Rainbow3, "6CCBFF", "165785", "00669D"),
            new PaletteEntry(JavaSemanticClassificationNames.Rainbow4, "8FD19E", "2E6A38", "2E7D32"),
            new PaletteEntry(JavaSemanticClassificationNames.Rainbow5, "FF9E64", "90501E", "AC4D00"),
            new PaletteEntry(JavaSemanticClassificationNames.Rainbow6, "A7A5FF", "515297", "5553A7")
        };
        // Called both by the required-role MEF listener and directly by the
        // semantic tagger. The property guards against duplicate subscriptions
        // to VSColorTheme.ThemeChanged for the same view.
        internal static void AttachView(IWpfTextView view,
            IClassificationFormatMapService maps,
            IClassificationTypeRegistryService types)
        {
            if (maps == null || types == null || view == null || view.IsClosed ||
                !view.TextBuffer.ContentType.IsOfType("java")) return;
            view.Properties.GetOrCreateSingletonProperty(Property, () =>
                new ViewSubscription(view, maps.GetClassificationFormatMap(view), types));
        }

        public void TextViewCreated(IWpfTextView view)
        {
            AttachView(view, Maps, Types);
        }
    }
}
