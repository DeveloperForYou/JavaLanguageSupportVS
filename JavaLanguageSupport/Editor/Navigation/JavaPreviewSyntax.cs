using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;

namespace JavaLanguageSupport.Editor.Navigation
{
    /// <summary>
    /// Lightweight lexical Java highlighter for the read-only Peek window.
    /// The actual editor continues to use TextMate/JDT LS semantic classifications.
    /// State is parsed from the beginning of the source, including lines outside
    /// the displayed range, so block comments and Java text blocks stay colored.
    /// </summary>
    internal static class JavaPreviewSyntax
    {
        private enum LexState { Code, BlockComment, TextBlock }

        private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal) {
            "abstract", "assert", "boolean", "break", "byte", "case", "catch", "char",
            "class", "const", "continue", "default", "do", "double", "else", "enum",
            "extends", "final", "finally", "float", "for", "goto", "if", "implements",
            "import", "instanceof", "int", "interface", "long", "native", "new",
            "package", "private", "protected", "public", "return", "short", "static",
            "strictfp", "super", "switch", "synchronized", "this", "throw", "throws",
            "transient", "try", "void", "volatile", "while", "record", "sealed",
            "permits", "var", "yield", "module", "open", "requires", "exports",
            "opens", "uses", "provides", "with", "to", "transitive", "non"
        };

        private sealed class Colors
        {
            internal readonly Brush Plain;
            internal readonly Brush Keyword;
            internal readonly Brush Type;
            internal readonly Brush Method;
            internal readonly Brush Comment;
            internal readonly Brush String;
            internal readonly Brush Number;
            internal readonly Brush Annotation;
            internal readonly Brush Gutter;
            internal readonly Brush ActiveLine;

            internal Colors(bool dark)
            {
                Plain = MakeBrush(dark ? 0xD4D4D4 : 0x242424);
                Keyword = MakeBrush(dark ? 0x569CD6 : 0x0000FF);
                Type = MakeBrush(dark ? 0x4EC9B0 : 0x267F99);
                Method = MakeBrush(dark ? 0xDCDCAA : 0x795E26);
                Comment = MakeBrush(dark ? 0x6A9955 : 0x008000);
                String = MakeBrush(dark ? 0xCE9178 : 0xA31515);
                Number = MakeBrush(dark ? 0xB5CEA8 : 0x098658);
                Annotation = MakeBrush(dark ? 0xC586C0 : 0xAF00DB);
                Gutter = MakeBrush(dark ? 0x858585 : 0x777777);
                ActiveLine = MakeBrush(dark ? 0x333D4A : 0xEAF2FA);
            }

            private static Brush MakeBrush(int rgb)
            {
                var result = new SolidColorBrush(Color.FromRgb(
                    (byte)((rgb >> 16) & 255), (byte)((rgb >> 8) & 255), (byte)(rgb & 255)));
                result.Freeze();
                return result;
            }
        }

        internal static FlowDocument Create(string[] lines, int first, int last, int selectedLine)
        {
            bool dark = IsDarkTheme();
            var colors = new Colors(dark);
            var document = new FlowDocument {
                FontFamily = new FontFamily("Consolas"), FontSize = 13,
                Foreground = colors.Plain,
                PagePadding = new Thickness(8)
            };

            // FlowDocument has no TextWrapping property. A generous page width
            // keeps normal Java lines unwrapped and enables horizontal scrolling.
            int maximumLine = 0;
            for (int n = Math.Max(0, first); n <= last && n < lines.Length; n++)
                maximumLine = Math.Max(maximumLine, lines[n].Length);
            document.PageWidth = Math.Min(20000, Math.Max(360, (maximumLine + 14) * 9));

            LexState state = LexState.Code;
            int width = (last + 1).ToString().Length;
            for (int n = 0; n <= last && n < lines.Length; n++)
            {
                // Parse preceding lines as well, even if not rendered.
                var paragraph = new Paragraph {
                    Margin = new Thickness(0), Padding = new Thickness(0),
                    LineHeight = 17
                };
                if (n >= first)
                {
                    string label = (n == selectedLine ? "> " : "  ") +
                        (n + 1).ToString().PadLeft(width) + "  ";
                    paragraph.Inlines.Add(new Run(label) { Foreground = colors.Gutter });
                    if (n == selectedLine) paragraph.Background = colors.ActiveLine;
                }
                HighlightLine(lines[n], n >= first ? paragraph : null, ref state, colors);
                if (n >= first)
                    document.Blocks.Add(paragraph);
            }
            return document;
        }

        private static bool IsDarkTheme()
        {
            try
            {
                var color = VSColorTheme.GetThemedColor(
                    EnvironmentColors.ToolWindowBackgroundColorKey);
                return (color.R * 299 + color.G * 587 + color.B * 114) < 128000;
            }
            catch (Exception) { return true; }
        }

        private static void Add(Paragraph line, string value, Brush foreground)
        {
            if (line == null || value.Length == 0) return;
            line.Inlines.Add(new Run(value) { Foreground = foreground });
        }

        private static bool IdentifierPart(char ch)
        {
            return char.IsLetterOrDigit(ch) || ch == '_' || ch == '$';
        }

        private static void HighlightLine(string code, Paragraph line, ref LexState state, Colors palette)
        {
            int i = 0;
            while (i < code.Length)
            {
                int start = i;
                if (state == LexState.BlockComment)
                {
                    int close = code.IndexOf("*/", i, StringComparison.Ordinal);
                    i = close < 0 ? code.Length : close + 2;
                    Add(line, code.Substring(start, i - start), palette.Comment);
                    if (close >= 0) state = LexState.Code;
                    continue;
                }
                if (state == LexState.TextBlock)
                {
                    int close = code.IndexOf("\"\"\"", i, StringComparison.Ordinal);
                    i = close < 0 ? code.Length : close + 3;
                    Add(line, code.Substring(start, i - start), palette.String);
                    if (close >= 0) state = LexState.Code;
                    continue;
                }
                if (i + 1 < code.Length && code[i] == '/' && code[i + 1] == '/')
                {
                    Add(line, code.Substring(i), palette.Comment);
                    break;
                }
                if (i + 1 < code.Length && code[i] == '/' && code[i + 1] == '*')
                {
                    int close = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = close < 0 ? code.Length : close + 2;
                    Add(line, code.Substring(start, i - start), palette.Comment);
                    state = close < 0 ? LexState.BlockComment : LexState.Code;
                    continue;
                }
                if (i + 2 < code.Length && code[i] == '"' &&
                    code[i + 1] == '"' && code[i + 2] == '"')
                {
                    int close = code.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                    i = close < 0 ? code.Length : close + 3;
                    Add(line, code.Substring(start, i - start), palette.String);
                    state = close < 0 ? LexState.TextBlock : LexState.Code;
                    continue;
                }
                if (code[i] == '"' || code[i] == '\'')
                {
                    char quote = code[i++];
                    while (i < code.Length)
                    {
                        if (code[i] == '\\' && i + 1 < code.Length) { i += 2; continue; }
                        if (code[i++] == quote) break;
                    }
                    Add(line, code.Substring(start, i - start), palette.String);
                    continue;
                }
                if (code[i] == '@' && i + 1 < code.Length && IdentifierPart(code[i + 1]))
                {
                    i += 2;
                    while (i < code.Length && IdentifierPart(code[i])) i++;
                    Add(line, code.Substring(start, i - start), palette.Annotation);
                    continue;
                }
                if (char.IsDigit(code[i]))
                {
                    i++;
                    while (i < code.Length && (IdentifierPart(code[i]) || code[i] == '.')) i++;
                    Add(line, code.Substring(start, i - start), palette.Number);
                    continue;
                }
                if (char.IsLetter(code[i]) || code[i] == '_' || code[i] == '$')
                {
                    i++;
                    while (i < code.Length && IdentifierPart(code[i])) i++;
                    string word = code.Substring(start, i - start);
                    Brush brush = palette.Plain;
                    if (Keywords.Contains(word)) brush = palette.Keyword;
                    else if (word == "true" || word == "false" || word == "null") brush = palette.Number;
                    else
                    {
                        int look = i;
                        while (look < code.Length && char.IsWhiteSpace(code[look])) look++;
                        if (look < code.Length && code[look] == '(') brush = palette.Method;
                        else if (char.IsUpper(word[0])) brush = palette.Type;
                    }
                    Add(line, word, brush);
                    continue;
                }
                i++;
                while (i < code.Length && !char.IsLetterOrDigit(code[i]) &&
                    code[i] != '_' && code[i] != '$' && code[i] != '@' &&
                    code[i] != '"' && code[i] != '\'' &&
                    !(i + 1 < code.Length && code[i] == '/' &&
                        (code[i + 1] == '/' || code[i + 1] == '*')))
                    i++;
                Add(line, code.Substring(start, i - start), palette.Plain);
            }
        }
    }
}
