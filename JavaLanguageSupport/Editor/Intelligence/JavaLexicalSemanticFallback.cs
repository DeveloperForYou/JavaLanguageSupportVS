using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Tagging;

namespace JavaLanguageSupport.Editor.Intelligence
{
    /// <summary>
    /// Lightweight lexical COLOR fallback when JDT LS does not advertise semanticTokens,
    /// or returns an incomplete token list. Never changes the source buffer.
    /// It is not a Java parser: semantic tokens always take precedence where present.
    /// Strings, character literals, text blocks and comments are intentionally skipped.
    /// </summary>
    internal static class JavaLexicalSemanticFallback
    {
        private sealed class Word
        {
            internal string Value;
            internal int Start;
            internal int Length;
            internal bool Directive;
            internal string DirectiveKind;
        }

        private sealed class Symbol
        {
            internal int Start;
            internal char Value;
        }

        private sealed class OpenBracket
        {
            internal int Start;
            internal char Value;
            internal int Level;
        }

        private static readonly string[] RainbowNames =
        {
            JavaSemanticClassificationNames.Rainbow1,
            JavaSemanticClassificationNames.Rainbow2,
            JavaSemanticClassificationNames.Rainbow3,
            JavaSemanticClassificationNames.Rainbow4,
            JavaSemanticClassificationNames.Rainbow5,
            JavaSemanticClassificationNames.Rainbow6
        };

        private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract", "assert", "boolean", "break", "byte", "case", "catch", "char",
            "class", "const", "continue", "default", "do", "double", "else", "enum",
            "extends", "final", "finally", "float", "for", "goto", "if", "implements",
            "import", "instanceof", "int", "interface", "long", "native", "new", "package",
            "private", "protected", "public", "return", "short", "static", "strictfp",
            "super", "switch", "synchronized", "this", "throw", "throws", "transient",
            "try", "void", "volatile", "while", "true", "false", "null", "var", "record",
            "sealed", "permits", "yield", "non", "module", "requires", "exports", "opens",
            "uses", "provides", "with", "transitive", "to", "when", "_"
        };

        // Reserved Java words are recolored by our editable VS classifications.
        // Directive keywords (package/import/static import) keep their existing
        // classification and are handled before this method is considered.
        private static string NewKeywordClassification(string value)
        {
            switch (value)
            {
                case "var":
                    return JavaKeywordClassificationNames.Var;
                case "boolean": case "byte": case "short": case "int":
                case "long": case "char": case "float": case "double": case "void":
                    return JavaKeywordClassificationNames.Primitive;
                case "public": case "protected": case "private":
                case "static": case "final": case "abstract": case "native":
                case "strictfp": case "transient": case "volatile": case "sealed":
                    return JavaKeywordClassificationNames.Modifier;
                case "true": case "false": case "null":
                    return JavaKeywordClassificationNames.Constant;
                default:
                    return JavaKeywordClassificationNames.Keyword;
            }
        }

        internal static List<ITagSpan<IClassificationTag>> Scan(ITextSnapshot snapshot,
            IClassificationTypeRegistryService registry, HashSet<int> semanticPositions)
        {
            var result = new List<ITagSpan<IClassificationTag>>();
            if (snapshot == null || registry == null || snapshot.Length > 1024 * 1024)
                return result;
            string source = snapshot.GetText();
            var symbols = new List<Symbol>();
            List<Word> words = ReadWords(source, symbols);
            var cached = new Dictionary<string, IClassificationType>(StringComparer.Ordinal);
            for (int index = 0; index < words.Count && result.Count < 40000; index++)
            {
                Word word = words[index];
                if (!word.Directive && Keywords.Contains(word.Value))
                {
                    // Scanner already excludes comments, literals and text blocks.
                    // Use distinct VS classifications for reserved Java words.
                    AddTag(result, snapshot, cached, registry,
                        NewKeywordClassification(word.Value), word.Start, word.Length);
                    continue;
                }
                // JDT LS is authoritative for normal Java identifiers. Directive
                // identifiers are handled here to separate import namespaces/types.
                if (!word.Directive && semanticPositions != null &&
                    semanticPositions.Contains(word.Start)) continue;

                string previous = index > 0 ? words[index - 1].Value : "";
                int before = PreviousVisible(source, word.Start - 1);
                int after = NextVisible(source, word.Start + word.Length);
                string classification;
                if (word.Directive)
                {
                    if (word.Value == "package" || word.Value == "import" ||
                        (word.Value == "static" && previous == "import"))
                        classification = JavaSemanticClassificationNames.DirectiveKeyword;
                    else if (word.DirectiveKind == "import" && LooksLikeType(word.Value))
                        classification = JavaSemanticClassificationNames.Type;
                    else
                        classification = JavaSemanticClassificationNames.Namespace;
                }
                else
                {
                    bool annotation = before >= 0 && source[before] == '@';
                    bool declaration = previous == "class" || previous == "interface" ||
                        previous == "enum" || previous == "record";
                    bool typeName = declaration || previous == "new" || previous == "extends" ||
                        previous == "implements" || previous == "instanceof" ||
                        previous == "throws" || LooksLikeType(word.Value);
                    bool method = after < source.Length && source[after] == '(';
                    bool methodReference = before >= 1 && source[before] == ':' &&
                        source[before - 1] == ':';

                    if (annotation)
                        classification = JavaSemanticClassificationNames.Annotation;
                    else if (typeName)
                        classification = JavaSemanticClassificationNames.Type;
                    else if (method || methodReference)
                        classification = JavaSemanticClassificationNames.Method;
                    else
                        classification = JavaSemanticClassificationNames.Variable;
                }
                AddTag(result, snapshot, cached, registry, classification,
                    word.Start, word.Length);
            }
            // A second list avoids treating punctuation as the "previous word":
            // the existing lexical method/type heuristics remain unchanged.
            // ReadWords excludes comments, strings, chars and text blocks.
            // Compute the nesting levels once per document (linear time).
            // Both ends of a correctly paired bracket receive the same color.
            // Unmatched brackets keep the standard customizable bracket color.
            Dictionary<int, int> matchedLevels = FindMatchedBracketLevels(symbols);
            foreach (Symbol symbol in symbols)
            {
                if (result.Count >= 60000) break;
                string classification;
                if (IsBracket(symbol.Value))
                {
                    int level;
                    classification = matchedLevels.TryGetValue(symbol.Start, out level)
                        ? RainbowNames[level]
                        : JavaSemanticClassificationNames.Bracket;
                }
                else
                    classification = JavaSemanticClassificationNames.Separator;
                AddTag(result, snapshot, cached, registry, classification, symbol.Start, 1);
            }
            return result;
        }

        private static Dictionary<int, int> FindMatchedBracketLevels(List<Symbol> symbols)
        {
            // Scanner has already excluded strings, text blocks, char literals
            // and both line/block comments. Mixed (), [] and {} share one stack.
            var matches = new Dictionary<int, int>();
            var openings = new Stack<OpenBracket>();
            foreach (Symbol symbol in symbols)
            {
                char value = symbol.Value;
                if (value == '(' || value == '[' || value == '{')
                {
                    openings.Push(new OpenBracket {
                        Start = symbol.Start,
                        Value = value,
                        Level = openings.Count % JavaSemanticClassificationNames.RainbowLevels
                    });
                }
                else if ((value == ')' || value == ']' || value == '}') &&
                    openings.Count != 0 && AreMatchingBrackets(openings.Peek().Value, value))
                {
                    OpenBracket opening = openings.Pop();
                    matches[opening.Start] = opening.Level;
                    matches[symbol.Start] = opening.Level;
                }
                // Never guess a pair for mismatched closing brackets.
            }
            return matches;
        }

        private static bool AreMatchingBrackets(char opening, char closing)
        {
            return (opening == '(' && closing == ')') ||
                (opening == '[' && closing == ']') ||
                (opening == '{' && closing == '}');
        }

        private static void AddTag(List<ITagSpan<IClassificationTag>> tags,
            ITextSnapshot snapshot, Dictionary<string, IClassificationType> cached,
            IClassificationTypeRegistryService registry, string name, int offset, int length)
        {
            IClassificationType resolved;
            if (!cached.TryGetValue(name, out resolved))
            {
                resolved = registry.GetClassificationType(name);
                cached.Add(name, resolved);
            }
            if (resolved == null) return;
            tags.Add(new TagSpan<IClassificationTag>(
                new SnapshotSpan(snapshot, offset, length), new ClassificationTag(resolved)));
        }

        private static bool IsBracket(char value)
        {
            return value == '(' || value == ')' || value == '[' || value == ']' ||
                value == '{' || value == '}';
        }

        private static bool LooksLikeType(string name)
        {
            if (string.IsNullOrEmpty(name) || !char.IsUpper(name[0])) return false;
            // MAX_VALUE is usually a constant; String, Runnable, URL and T are types.
            if (name.IndexOf('_') >= 0) return false;
            return true;
        }

        private static int PreviousVisible(string source, int index)
        {
            while (index >= 0 && char.IsWhiteSpace(source[index])) index--;
            return index;
        }

        private static int NextVisible(string source, int index)
        {
            while (index < source.Length && char.IsWhiteSpace(source[index])) index++;
            return index;
        }

        private static bool IdentifierStart(char value)
        {
            return char.IsLetter(value) || value == '_' || value == '$';
        }

        private static bool IdentifierPart(char value)
        {
            return char.IsLetterOrDigit(value) || value == '_' || value == '$';
        }

        private static List<Word> ReadWords(string text, List<Symbol> symbols)
        {
            var words = new List<Word>();
            int length = text.Length;
            int i = 0;
            string directive = null;
            while (i < length && words.Count < 100000)
            {
                char c = text[i];
                if (c == '/' && i + 1 < length && text[i + 1] == '/')
                {
                    i += 2;
                    while (i < length && text[i] != '\n' && text[i] != '\r') i++;
                    continue;
                }
                if (c == '/' && i + 1 < length && text[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < length && !(text[i] == '*' && text[i + 1] == '/')) i++;
                    i = Math.Min(length, i + 2);
                    continue;
                }
                if (c == '"' && i + 2 < length && text[i + 1] == '"' && text[i + 2] == '"')
                {
                    i += 3;
                    while (i + 2 < length && !(text[i] == '"' && text[i + 1] == '"' && text[i + 2] == '"')) i++;
                    i = Math.Min(length, i + 3);
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    char quote = c;
                    i++;
                    while (i < length)
                    {
                        if (text[i] == '\\') { i = Math.Min(length, i + 2); continue; }
                        if (text[i++] == quote) break;
                    }
                    continue;
                }
                if (c == ';')
                {
                    symbols.Add(new Symbol { Start = i, Value = c });
                    directive = null;
                    i++;
                    continue;
                }
                if (IsBracket(c) || c == ',' || c == '.')
                {
                    // Do not color a decimal point in numeric literals (3.14).
                    if (c != '.' || i == 0 || i + 1 >= length ||
                        !char.IsDigit(text[i - 1]) || !char.IsDigit(text[i + 1]))
                        symbols.Add(new Symbol { Start = i, Value = c });
                    i++;
                    continue;
                }
                if (IdentifierStart(c))
                {
                    int from = i++;
                    while (i < length && IdentifierPart(text[i])) i++;
                    string name = text.Substring(from, i - from);
                    if (name == "import" || name == "package") directive = name;
                    words.Add(new Word { Value = name, Start = from, Length = i - from,
                        Directive = directive != null, DirectiveKind = directive });
                    continue;
                }
                i++;
            }
            return words;
        }
    }
}
