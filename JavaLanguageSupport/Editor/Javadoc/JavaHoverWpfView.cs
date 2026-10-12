using System;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json.Linq;
using Block = Markdig.Syntax.Block;
using Inline = Markdig.Syntax.Inlines.Inline;
using Table = Markdig.Extensions.Tables.Table;

namespace JavaLanguageSupport.Editor.Javadoc
{
    // Solo dati in questo oggetto: GetQuickInfoItemAsync viene eseguito su un thread in background.
    // La factory MEF costruisce i controlli WPF sul thread UI quando Visual Studio presenta il tooltip.
    internal sealed class JavaHoverWpfContent : IInteractiveQuickInfoContent
    {
        internal readonly string Markdown;
        internal FrameworkElement View;

        internal JavaHoverWpfContent(string markdown)
        {
            Markdown = markdown ?? string.Empty;
        }

        public bool IsMouseOverAggregated => View != null && View.IsMouseOver;
        public bool KeepQuickInfoOpen => View != null && (View.IsMouseOver || View.IsKeyboardFocusWithin);

        // Se Visual Studio non carica la factory MEF, il convertitore predefinito mostra almeno il testo.
        public override string ToString() => Markdown;
    }

    internal static class JavaHoverLspMarkdown
    {
        internal static string Read(JToken hover)
        {
            JObject obj = hover as JObject;
            JToken contents = obj == null ? null : obj["contents"];
            if (contents == null || contents.Type == JTokenType.Null) return null;
            var builder = new StringBuilder();
            JArray parts = contents as JArray;
            if (parts != null)
            {
                foreach (JToken item in parts) Append(builder, item);
            }
            else Append(builder, contents);
            return builder.ToString().Trim();
        }

        private static void Append(StringBuilder b, JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return;
            JObject obj = token as JObject;
            string value = token.Type == JTokenType.String ? (string)token :
                obj == null ? null : (string)obj["value"];
            if (string.IsNullOrWhiteSpace(value)) return;
            if (obj != null && (string)obj["language"] == "java" &&
                !value.TrimStart().StartsWith("```", StringComparison.Ordinal))
                value = "```java\n" + value + "\n```";
            if (b.Length > 0) b.AppendLine().AppendLine();
            b.Append(value);
        }
    }

    // L'export converte il modello Quick Info in WPF. Non creare UIElement nel metodo LSP asincrono.
    [Export(typeof(IViewElementFactory))]
    [Name("Java QuickInfo WPF View Factory")]
    [TypeConversion(from: typeof(JavaHoverWpfContent), to: typeof(UIElement))]
    [Order(Before = "Default object converter")]
    internal sealed class JavaHoverWpfViewFactory : IViewElementFactory
    {
        public TView CreateViewElement<TView>(ITextView textView, object model) where TView : class
        {
            var data = model as JavaHoverWpfContent;
            if (data == null) return null;
            try
            {
                FrameworkElement control = JavaHoverWpfRenderer.Create(data.Markdown);
                data.View = control;
                return control as TView;
            }
            catch (Exception ex)
            {
                // Non interrompere Quick Info per un errore di rendering Markdown.
                Debug.WriteLine("Java QuickInfo WPF: " + ex);
                var fallback = new TextBlock
                {
                    Text = data.Markdown,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 700
                };
                data.View = fallback;
                return fallback as TView;
            }
        }
    }

    // Parser CommonMark vero: gestisce anche ``` annidati nei > blockquote di JDT LS.
    internal static class JavaHoverWpfRenderer
    {
        private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();

        private static readonly Regex JavaTokens = new Regex(
            "//[^\\r\\n]*|\\\"(?:\\\\.|[^\\\"\\\\])*\\\"|'(?:\\\\.|[^'\\\\])*'|\\b(?:abstract|assert|boolean|break|byte|case|catch|char|class|const|continue|default|do|double|else|enum|extends|final|finally|float|for|if|implements|import|instanceof|int|interface|long|native|new|package|private|protected|public|record|return|short|static|strictfp|super|switch|synchronized|this|throw|throws|transient|try|var|void|volatile|while|null|true|false)\\b|\\b[0-9]+\\b",
            RegexOptions.Compiled);

        private sealed class Palette
        {
            internal Brush Background;
            internal Brush Foreground;
            internal Brush Muted;
            internal Brush Border;
            internal Brush Link;
            internal Brush CodeBackground;
            internal Brush Keyword;
            internal Brush Literal;
            internal Brush Comment;
        }

        private static Brush BrushFrom(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private static Palette GetPalette()
        {
            System.Drawing.Color background;
            System.Drawing.Color foreground;
            try
            {
                background = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
                foreground = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowTextColorKey);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Java QuickInfo VS theme: " + ex.Message);
                background = System.Drawing.SystemColors.Window;
                foreground = System.Drawing.SystemColors.WindowText;
            }
            bool dark = background.R * .299 + background.G * .587 + background.B * .114 < 130;
            return new Palette
            {
                Background = BrushFrom(background.R, background.G, background.B),
                Foreground = BrushFrom(foreground.R, foreground.G, foreground.B),
                Muted = dark ? BrushFrom(175, 175, 175) : BrushFrom(95, 95, 95),
                Border = dark ? BrushFrom(75, 75, 75) : BrushFrom(210, 210, 210),
                Link = dark ? BrushFrom(100, 185, 235) : BrushFrom(0, 90, 165),
                CodeBackground = dark ? BrushFrom(28, 28, 30) : BrushFrom(244, 244, 246),
                Keyword = dark ? BrushFrom(198, 153, 231) : BrushFrom(127, 0, 126),
                Literal = dark ? BrushFrom(206, 152, 117) : BrushFrom(154, 65, 20),
                Comment = dark ? BrushFrom(110, 165, 104) : BrushFrom(0, 110, 45)
            };
        }

        internal static FrameworkElement Create(string markdown)
        {
            // JDT LS restituisce Markdown; non trattarlo come testo formattato a mano.
            string normalized = (markdown ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            if (normalized.Length > 250000) normalized = normalized.Substring(0, 250000) + "\n\n…";
            MarkdownDocument parsed = Markdown.Parse(normalized, Pipeline);
            Palette palette = GetPalette();

            Block signature = null;
            string title = FindTitle(parsed, out signature);
            var root = new Border
            {
                MinWidth = 440,
                MaxWidth = Math.Min(790, Math.Max(440, SystemParameters.WorkArea.Width * .72)),
                MaxHeight = Math.Min(540, Math.Max(270, SystemParameters.WorkArea.Height * .70)),
                Background = palette.Background,
                BorderBrush = palette.Border,
                BorderThickness = new Thickness(1),
                SnapsToDevicePixels = true
            };
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.Child = grid;

            var header = new Border
            {
                Padding = new Thickness(14, 10, 14, 10),
                Background = palette.Background,
                BorderBrush = palette.Border,
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            var heading = new TextBlock
            {
                Text = title ?? "Java · Javadoc",
                TextWrapping = TextWrapping.Wrap,
                Foreground = palette.Foreground,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold
            };
            header.Child = heading;
            Grid.SetRow(header, 0);
            grid.Children.Add(header);

            var document = new FlowDocument
            {
                PagePadding = new Thickness(14, 10, 14, 12),
                ColumnWidth = 740,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12.5,
                Foreground = palette.Foreground,
                Background = palette.Background
            };
            foreach (Block block in parsed)
            {
                if (object.ReferenceEquals(block, signature)) continue;
                AppendBlock(block, document.Blocks, palette);
            }
            if (document.Blocks.Count == 0)
                document.Blocks.Add(new Paragraph(new Run("Nessuna descrizione disponibile.")));
            var viewer = new FlowDocumentScrollViewer
            {
                Document = document,
                IsToolBarVisible = false,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = palette.Background,
                Foreground = palette.Foreground,
                MaxHeight = 478
            };
            Grid.SetRow(viewer, 1);
            grid.Children.Add(viewer);
            return root;
        }

        private static string FindTitle(MarkdownDocument doc, out Block signature)
        {
            signature = null;
            foreach (Block block in doc)
            {
                string first = null;
                if (block is ParagraphBlock p) first = PlainText(p.Inline);
                else if (block is FencedCodeBlock fenced) first = fenced.Lines.ToString().Trim();
                if (string.IsNullOrWhiteSpace(first)) continue;
                first = first.Trim();
                // Firma di una classe/metodo JDT LS, non un intero paragrafo descrittivo.
                if (first.Length <= 180 && first.IndexOf('\n') < 0 &&
                    (first.StartsWith("java.", StringComparison.Ordinal) ||
                     first.Contains("(") || first.Contains("<") ||
                     first.StartsWith("public ", StringComparison.Ordinal) ||
                     first.StartsWith("class ", StringComparison.Ordinal) ||
                     first.StartsWith("interface ", StringComparison.Ordinal)))
                {
                    signature = block;
                    return first;
                }
                break;
            }
            return null;
        }

        private static string PlainText(ContainerInline inline)
        {
            if (inline == null) return string.Empty;
            var b = new StringBuilder();
            for (Inline child = inline.FirstChild; child != null; child = child.NextSibling)
            {
                if (child is LiteralInline lit) b.Append(lit.Content.ToString());
                else if (child is CodeInline code) b.Append(code.Content);
                else if (child is ContainerInline nested) b.Append(PlainText(nested));
            }
            return b.ToString();
        }

        private static void AppendBlock(Block block, BlockCollection target, Palette colors)
        {
            if (block is FencedCodeBlock fenced)
            {
                AddCode(target, fenced.Lines.ToString(), colors);
                return;
            }
            if (block is CodeBlock code)
            {
                AddCode(target, code.Lines.ToString(), colors);
                return;
            }
            if (block is Table table)
            {
                AddTable(target, table, colors);
                return;
            }
            if (block is QuoteBlock quote)
            {
                var group = new Section
                {
                    Margin = new Thickness(7, 6, 2, 8),
                    Padding = new Thickness(10, 0, 0, 0),
                    BorderBrush = colors.Border,
                    BorderThickness = new Thickness(3, 0, 0, 0)
                };
                foreach (Block child in quote) AppendBlock(child, group.Blocks, colors);
                target.Add(group);
                return;
            }
            if (block is ListBlock list)
            {
                var wpfList = new System.Windows.Documents.List
                {
                    MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(10, 3, 0, 7)
                };
                foreach (Block item in list)
                {
                    var li = new ListItem();
                    if (item is ContainerBlock container)
                        foreach (Block child in container) AppendBlock(child, li.Blocks, colors);
                    if (li.Blocks.Count == 0) li.Blocks.Add(new Paragraph());
                    wpfList.ListItems.Add(li);
                }
                target.Add(wpfList);
                return;
            }
            if (block is HeadingBlock heading)
            {
                var p = new Paragraph
                {
                    FontWeight = FontWeights.SemiBold,
                    FontSize = heading.Level <= 2 ? 15 : 13,
                    Margin = new Thickness(0, 12, 0, 5)
                };
                AppendInline(heading.Inline, p.Inlines, colors);
                target.Add(p);
                return;
            }
            if (block is ParagraphBlock paragraph)
            {
                var p = new Paragraph { Margin = new Thickness(0, 2, 0, 8) };
                AppendInline(paragraph.Inline, p.Inlines, colors);
                target.Add(p);
                return;
            }
            if (block is HtmlBlock html)
            {
                // Il server può inviare una parte del Javadoc come HTML. Mostra il testo
                // senza eseguire markup o caricare risorse esterne.
                string plain = WebUtility.HtmlDecode(Regex.Replace(html.Lines.ToString(), "<[^>]+>", " "));
                if (!string.IsNullOrWhiteSpace(plain))
                    target.Add(new Paragraph(new Run(plain.Trim())) { Margin = new Thickness(0, 2, 0, 8) });
                return;
            }
            if (block is ThematicBreakBlock)
            {
                target.Add(new Paragraph { BorderBrush = colors.Border, BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 8, 0, 8) });
                return;
            }
            // Markdig raggruppa blockquote/liste/tabelle in nodi annidati.
            if (block is ContainerBlock groupBlock)
            {
                foreach (Block child in groupBlock) AppendBlock(child, target, colors);
            }
        }

        private static void AddCode(BlockCollection target, string code, Palette colors)
        {
            var rich = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                TextWrapping = TextWrapping.NoWrap,
                Foreground = colors.Foreground,
                Padding = new Thickness(11, 9, 11, 9)
            };
            int previous = 0;
            foreach (Match match in JavaTokens.Matches(code))
            {
                if (match.Index > previous)
                    rich.Inlines.Add(new Run(code.Substring(previous, match.Index - previous)));
                string t = match.Value;
                Brush brush = t.StartsWith("//", StringComparison.Ordinal) ? colors.Comment :
                    (t[0] == '"' || t[0] == '\'') ? colors.Literal :
                    char.IsDigit(t[0]) ? colors.Literal : colors.Keyword;
                rich.Inlines.Add(new Run(t) { Foreground = brush });
                previous = match.Index + match.Length;
            }
            if (previous < code.Length) rich.Inlines.Add(new Run(code.Substring(previous)));
            var scroller = new ScrollViewer
            {
                Content = rich,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = colors.CodeBackground,
                MaxWidth = 705,
                CanContentScroll = false
            };
            target.Add(new BlockUIContainer(new Border
            {
                Child = scroller,
                BorderBrush = colors.Border,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 4, 0, 10)
            }));
        }

        private static void AddTable(BlockCollection target, Table source, Palette colors)
        {
            var table = new System.Windows.Documents.Table
            {
                CellSpacing = 0,
                BorderThickness = new Thickness(1),
                BorderBrush = colors.Border,
                Margin = new Thickness(0, 6, 0, 10)
            };
            var rowGroup = new TableRowGroup();
            table.RowGroups.Add(rowGroup);
            foreach (Block r in source)
            {
                if (!(r is Markdig.Extensions.Tables.TableRow row)) continue;
                var wrow = new System.Windows.Documents.TableRow();
                foreach (Block c in row)
                {
                    if (!(c is Markdig.Extensions.Tables.TableCell cell)) continue;
                    var wcell = new System.Windows.Documents.TableCell
                    {
                        BorderBrush = colors.Border,
                        BorderThickness = new Thickness(0, 0, 1, 1),
                        Padding = new Thickness(7, 3, 7, 3)
                    };
                    foreach (Block child in cell) AppendBlock(child, wcell.Blocks, colors);
                    if (wcell.Blocks.Count == 0) wcell.Blocks.Add(new Paragraph());
                    if (row.IsHeader) wcell.FontWeight = FontWeights.SemiBold;
                    wrow.Cells.Add(wcell);
                }
                rowGroup.Rows.Add(wrow);
            }
            target.Add(table);
        }

        private static void AppendInline(ContainerInline from, InlineCollection to, Palette colors)
        {
            if (from == null) return;
            for (Inline node = from.FirstChild; node != null; node = node.NextSibling)
            {
                if (node is LiteralInline literal)
                    to.Add(new Run(literal.Content.ToString()));
                else if (node is CodeInline code)
                    to.Add(new Run(code.Content)
                    {
                        FontFamily = new FontFamily("Consolas"),
                        Background = colors.CodeBackground
                    });
                else if (node is EmphasisInline em)
                {
                    System.Windows.Documents.Span span = em.DelimiterCount >= 2
                        ? (System.Windows.Documents.Span)new Bold() : new Italic();
                    AppendInline(em, span.Inlines, colors);
                    to.Add(span);
                }
                else if (node is LinkInline link)
                {
                    var uri = SafeExternalUri(link.Url);
                    if (uri == null || link.IsImage)
                        AppendInline(link, to, colors);
                    else
                    {
                        var wpfLink = new Hyperlink { NavigateUri = uri, Foreground = colors.Link };
                        AppendInline(link, wpfLink.Inlines, colors);
                        wpfLink.RequestNavigate += (sender, args) =>
                        {
                            try { Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true }); }
                            catch (Exception ex) { Debug.WriteLine("Java QuickInfo hyperlink: " + ex.Message); }
                            args.Handled = true;
                        };
                        to.Add(wpfLink);
                    }
                }
                else if (node is AutolinkInline auto)
                {
                    Uri uri = SafeExternalUri(auto.Url);
                    if (uri == null) to.Add(new Run(auto.Url));
                    else
                    {
                        var wpfLink = new Hyperlink(new Run(auto.Url)) { NavigateUri = uri, Foreground = colors.Link };
                        wpfLink.RequestNavigate += (sender, args) =>
                        {
                            try { Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true }); }
                            catch (Exception ex) { Debug.WriteLine("Java QuickInfo hyperlink: " + ex.Message); }
                            args.Handled = true;
                        };
                        to.Add(wpfLink);
                    }
                }
                else if (node is HtmlEntityInline entity)
                    to.Add(new Run(entity.Transcoded.ToString()));
                else if (node is LineBreakInline)
                    to.Add(new LineBreak());
                else if (node is HtmlInline html)
                {
                    if (html.Tag.StartsWith("<br", StringComparison.OrdinalIgnoreCase)) to.Add(new LineBreak());
                    // Non eseguire markup HTML non attendibile dal language server.
                }
                else if (node is ContainerInline group)
                    AppendInline(group, to, colors);
            }
        }

        private static Uri SafeExternalUri(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return null;
            return uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp ? uri : null;
        }
    }
}
