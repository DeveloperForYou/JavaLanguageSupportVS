using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using Microsoft.VisualStudio.Utilities;

namespace JavaLanguageSupport
{
    /// <summary>
    /// Handles Enter inside Java block comments. At '/**|' produces a Javadoc
    /// skeleton, with @param and @return tags when a following method signature
    /// can be recognized. Subsequent Enters continue the leading ' * '.
    /// This is deliberately independent from JDT LS, imports and brace pairing.
    /// </summary>
    [Export(typeof(ICommandHandler))]
    [Name(nameof(JavaJavadocCommandHandler))]
    [ContentType("java")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class JavaJavadocCommandHandler : IChainedCommandHandler<ReturnKeyCommandArgs>
    {
        public string DisplayName { get { return "Java: documentazione Javadoc automatica"; } }

        public CommandState GetCommandState(
            ReturnKeyCommandArgs args, Func<CommandState> nextCommandHandler)
        {
            return nextCommandHandler();
        }

        public void ExecuteCommand(
            ReturnKeyCommandArgs args,
            Action nextCommandHandler,
            CommandExecutionContext executionContext)
        {
            if (!ReferenceEquals(args.SubjectBuffer, args.TextView.TextBuffer) ||
                !args.TextView.Selection.IsEmpty)
            {
                nextCommandHandler();
                return;
            }

            ITextBuffer buffer = args.SubjectBuffer;
            ITextSnapshot snapshot = buffer.CurrentSnapshot;
            int pos = args.TextView.Caret.Position.BufferPosition.Position;
            if (pos < 0 || pos > snapshot.Length || buffer.IsReadOnly(pos))
            {
                nextCommandHandler();
                return;
            }

            CommentInfo comment = FindOpenBlockComment(snapshot, pos);
            if (comment == null)
            {
                nextCommandHandler();
                return;
            }

            ITextSnapshotLine line = snapshot.GetLineFromPosition(pos);
            string beforeCaret = snapshot.GetText(line.Start.Position, pos - line.Start.Position);
            string afterCaret = snapshot.GetText(pos, line.End.Position - pos);

            // The previous auto-pair handler can have inserted '*/' already.
            // Transform ONLY an otherwise empty '/**|*/' (or '/**|') opener.
            Match opener = Regex.Match(beforeCaret, @"^(?<indent>[ \t]*)/\*\*$");
            if (comment.IsJavadoc && opener.Success &&
                (afterCaret.Trim().Length == 0 || afterCaret.Trim() == "*/"))
            {
                string indent = opener.Groups["indent"].Value;
                string newline = GetNewline(snapshot, line);
                IList<string> tags = JavaDocSignatureTags.FromFollowingDeclaration(snapshot, line.LineNumber + 1);

                var template = new StringBuilder();
                template.Append(indent).Append("/**").Append(newline);
                template.Append(indent).Append(" * ");
                if (tags.Count > 0)
                {
                    template.Append(newline).Append(indent).Append(" *");
                    foreach (string tag in tags)
                        template.Append(newline).Append(indent).Append(" * ").Append(tag);
                }
                template.Append(newline).Append(indent).Append(" */");

                int start = line.Start.Position;
                ITextSnapshot changed = buffer.Replace(new Span(start, line.Length),template.ToString());
                int caretOffset = indent.Length + 3 + newline.Length + indent.Length + 3;
                args.TextView.Caret.MoveTo(new SnapshotPoint(changed, start + caretOffset));
                WriteLog("Creato Javadoc; tag generati=" + tags.Count);
                return;
            }

            // Do not continue an asterisk if Enter is pressed on the '*/' line.
            bool atClosingLine = Regex.IsMatch(line.GetText(), @"^\s*\*/\s*$");
            nextCommandHandler();

            if (atClosingLine)
                return;

            // The native editor may already insert the ' * ' prefix. Never duplicate it.
            ITextSnapshot updated = buffer.CurrentSnapshot;
            int newPos = args.TextView.Caret.Position.BufferPosition.Position;
            if (newPos < 0 || newPos > updated.Length || buffer.IsReadOnly(newPos))
                return;

            ITextSnapshotLine newLine = updated.GetLineFromPosition(newPos);
            string left = updated.GetText(newLine.Start.Position, newPos - newLine.Start.Position);
            string right = updated.GetText(newPos, newLine.End.Position - newPos);
            if (!IsHorizontalWhitespace(left))
                return;

            CommentInfo activeComment = FindOpenBlockComment(updated, newPos);
            if (activeComment == null)
                return;

            // Do not rewrite a closing line (also handles Enter directly before '*/').
            if (right.TrimStart().StartsWith("*/", StringComparison.Ordinal))
                return;

            string wanted = activeComment.Indent + " * ";
            if (left == wanted)
                return;
            // The built-in handler may insert a prefix to the right of the caret.
            if (right.TrimStart().StartsWith("*", StringComparison.Ordinal))
                return;

            ITextSnapshot inserted = buffer.Replace(new Span(newLine.Start.Position, left.Length),wanted);
            args.TextView.Caret.MoveTo(new SnapshotPoint(inserted, newLine.Start.Position + wanted.Length));
            WriteLog("Continuazione riga commento");
        }

        private static void WriteLog(string message)
        {
            try
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "JavaLanguageSupportVS", "logs");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "editor-javadoc.log"),
                    DateTime.Now.ToString("O") + " | " + message + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static string GetNewline(ITextSnapshot snapshot, ITextSnapshotLine line)
        {
            if (line.LineBreakLength > 0)
                return line.GetLineBreakText();
            if (line.LineNumber > 0)
            {
                ITextSnapshotLine previous = snapshot.GetLineFromLineNumber(line.LineNumber - 1);
                if (previous.LineBreakLength > 0) return previous.GetLineBreakText();
            }
            return Environment.NewLine;
        }

        private static bool IsHorizontalWhitespace(string s)
        {
            foreach (char c in s)
                if (c != ' ' && c != '\t') return false;
            return true;
        }

        // Exposed to the WPF Enter fallback so we reuse the same lexical
        // context detection, rather than guessing from the closest /* token.
        internal static bool TryGetOpenBlockComment(
            ITextSnapshot snapshot, int caretPosition,
            out string indent, out bool isJavadoc)
        {
            CommentInfo info = FindOpenBlockComment(snapshot, caretPosition);
            if (info == null)
            {
                indent = null;
                isJavadoc = false;
                return false;
            }
            indent = info.Indent;
            isJavadoc = info.IsJavadoc;
            return true;
        }

        private sealed class CommentInfo
        {
            public bool IsJavadoc;
            public string Indent;
        }

        private enum JavaLexState { Code, LineComment, BlockComment, String, Character, TextBlock }

        // Lightweight lexical context: prevents expanding Enter inside Java strings,
        // char literals, text blocks or already-closed comments.
        private static CommentInfo FindOpenBlockComment(ITextSnapshot text, int end)
        {
            JavaLexState state = JavaLexState.Code;
            CommentInfo active = null;
            for (int i = 0; i < end; i++)
            {
                char c = text[i];
                char n = i + 1 < end ? text[i + 1] : '\0';
                if (state == JavaLexState.Code)
                {
                    if (c == '/' && n == '/') { state = JavaLexState.LineComment; i++; }
                    else if (c == '/' && n == '*')
                    {
                        state = JavaLexState.BlockComment;
                        ITextSnapshotLine openLine = text.GetLineFromPosition(i);
                        string left = text.GetText(openLine.Start.Position, i - openLine.Start.Position);
                        active = new CommentInfo
                        {
                            IsJavadoc = i + 2 < text.Length && text[i + 2] == '*',
                            Indent = Regex.Match(left, @"^[ \t]*").Value
                        };
                        i++;
                    }
                    else if (c == '"')
                    {
                        if (i + 2 < end && text[i + 1] == '"' && text[i + 2] == '"')
                        { state = JavaLexState.TextBlock; i += 2; }
                        else state = JavaLexState.String;
                    }
                    else if (c == '\'') state = JavaLexState.Character;
                }
                else if (state == JavaLexState.LineComment)
                {
                    if (c == '\r' || c == '\n') state = JavaLexState.Code;
                }
                else if (state == JavaLexState.BlockComment)
                {
                    if (c == '*' && n == '/') { state = JavaLexState.Code; active = null; i++; }
                }
                else if (state == JavaLexState.TextBlock)
                {
                    if (c == '"' && i + 2 < end && text[i + 1] == '"' && text[i + 2] == '"')
                    { state = JavaLexState.Code; i += 2; }
                }
                else if (c == '\\') i++;
                else if ((state == JavaLexState.String && c == '"') ||
                         (state == JavaLexState.Character && c == '\'')) state = JavaLexState.Code;
            }
            return state == JavaLexState.BlockComment ? active : null;
        }
    }

    /// <summary>
    /// Conservative signature-based Javadoc tag generator. Does not claim to
    /// fully parse Java: in uncertain contexts it generates only the skeleton.
    /// </summary>
    internal static class JavaDocSignatureTags
    {
        private static readonly Regex IdentifierAtEnd = new Regex(
            @"(?<id>[A-Za-z_$][\w$]*)\s*$", RegexOptions.Compiled);
        private static readonly Regex SimpleLeadingAnnotation = new Regex(
            @"^\s*@(?:[A-Za-z_$][\w$]*\.)*[A-Za-z_$][\w$]*(?:\([^()]*\))?\s*",
            RegexOptions.Compiled);
        private static readonly Regex ParameterIdentifier = new Regex(
            @"(?<![\w$])(?<id>[A-Za-z_$][\w$]*)\s*(?:\[\s*\]\s*)*$", RegexOptions.Compiled);

        public static IList<string> FromFollowingDeclaration(ITextSnapshot snapshot, int nextLine)
        {
            var result = new List<string>();
            string declaration = GetDeclaration(snapshot, nextLine);
            if (string.IsNullOrEmpty(declaration)) return result;

            // Ignore leading method annotations (including simple annotation arguments).
            declaration = RemoveLeadingAnnotations(declaration);

            int open = declaration.IndexOf('(');
            if (open < 0 || declaration.Substring(0, open).Contains("=")) return result;
            int close = FindMatchingParen(declaration, open);
            if (close < 0) return result;

            string before = declaration.Substring(0, open).TrimEnd();
            if (Regex.IsMatch(before, @"\b(?:class|interface|enum|record|new)\b"))
                return result;
            Match name = IdentifierAtEnd.Match(before);
            if (!name.Success) return result;
            string methodName = name.Groups["id"].Value;
            if (Regex.IsMatch(methodName, @"^(if|for|while|switch|catch|synchronized)$"))
                return result;

            string prefix = before.Substring(0, name.Index).Trim();
            string parameters = declaration.Substring(open + 1, close - open - 1);
            if (parameters.IndexOf("->", StringComparison.Ordinal) >= 0) return result;

            // Optional generic method parameters: public <T, U extends Number> T m(...)
            Match typeArgs = Regex.Match(prefix,
                @"^(?:(?:public|protected|private|static|final|abstract|synchronized|native|default|strictfp)\s+)*<(?<vars>[^>]+)>(?:\s|$)");
            if (typeArgs.Success)
            {
                foreach (string part in SplitTopLevel(typeArgs.Groups["vars"].Value))
                {
                    Match id = Regex.Match(part.Trim(), @"^[A-Za-z_$][\w$]*");
                    if (id.Success) result.Add("@param <" + id.Value + "> ");
                }
            }

            foreach (string parameter in SplitTopLevel(parameters))
            {
                if (string.IsNullOrWhiteSpace(parameter)) continue;
                string clean = Regex.Replace(parameter, @"@(?:[A-Za-z_$][\w$]*\.)*[A-Za-z_$][\w$]*(?:\([^()]*\))?\s*", "");
                Match id = ParameterIdentifier.Match(clean);
                if (id.Success && id.Groups["id"].Value != "this")
                    result.Add("@param " + id.Groups["id"].Value + " ");
            }

            // Constructors have no return type; remove method modifiers and a
            // simple <T> declaration to differentiate them from methods.
            string withoutModifiers = Regex.Replace(prefix,
                @"\b(public|protected|private|static|final|abstract|synchronized|native|default|strictfp)\b", "").Trim();
            withoutModifiers = Regex.Replace(withoutModifiers, @"^<[^>]+>\s*", "").Trim();
            bool constructor = withoutModifiers.Length == 0;
            bool returnsVoid = Regex.IsMatch(prefix, @"\bvoid\s*$");
            if (!constructor && !returnsVoid)
                result.Add("@return ");

            return result;
        }

        private static string GetDeclaration(ITextSnapshot snapshot, int firstLine)
        {
            var builder = new StringBuilder();
            int lines = Math.Min(snapshot.LineCount, firstLine + 20);
            for (int i = firstLine; i < lines && builder.Length < 4000; i++)
            {
                string part = snapshot.GetLineFromLineNumber(i).GetText().Trim();
                if (part.Length == 0 || part.StartsWith("//", StringComparison.Ordinal)) continue;
                if (part.StartsWith("/**", StringComparison.Ordinal) ||
                    part.StartsWith("/*", StringComparison.Ordinal)) return string.Empty;
                builder.Append(part).Append(' ');
                string accumulated = builder.ToString();
                // Do not mistake @Annotation(value) for the method declaration.
                string withoutAnnotations = RemoveLeadingAnnotations(accumulated);
                int open = withoutAnnotations.IndexOf('(');
                if (open >= 0 && FindMatchingParen(withoutAnnotations, open) >= 0)
                    return accumulated;
                if (open < 0 && (part.IndexOf('{') >= 0 || part.IndexOf(';') >= 0))
                    return accumulated;
            }
            return builder.ToString();
        }

        private static string RemoveLeadingAnnotations(string declaration)
        {
            while (true)
            {
                Match annotation = SimpleLeadingAnnotation.Match(declaration);
                if (!annotation.Success || annotation.Length == 0) return declaration.TrimStart();
                declaration = declaration.Substring(annotation.Length);
            }
        }

        private static int FindMatchingParen(string text, int open)
        {
            int depth = 0;
            bool inString = false, inChar = false, escaped = false;
            for (int i = open; i < text.Length; i++)
            {
                char c = text[i];
                if (escaped) { escaped = false; continue; }
                if ((inString || inChar) && c == '\\') { escaped = true; continue; }
                if (inString) { if (c == '"') inString = false; continue; }
                if (inChar) { if (c == '\'') inChar = false; continue; }
                if (c == '"') { inString = true; continue; }
                if (c == '\'') { inChar = true; continue; }
                if (c == '(') depth++;
                if (c == ')' && --depth == 0) return i;
            }
            return -1;
        }

        private static IEnumerable<string> SplitTopLevel(string list)
        {
            int angle = 0, round = 0, square = 0, curly = 0, start = 0;
            bool inString = false, inChar = false, escaped = false;
            for (int i = 0; i < list.Length; i++)
            {
                char c = list[i];
                if (escaped) { escaped = false; continue; }
                if ((inString || inChar) && c == '\\') { escaped = true; continue; }
                if (inString) { if (c == '"') inString = false; continue; }
                if (inChar) { if (c == '\'') inChar = false; continue; }
                if (c == '"') { inString = true; continue; }
                if (c == '\'') { inChar = true; continue; }
                if (c == '<') angle++;
                else if (c == '>' && angle > 0) angle--;
                else if (c == '(') round++;
                else if (c == ')' && round > 0) round--;
                else if (c == '[') square++;
                else if (c == ']' && square > 0) square--;
                else if (c == '{') curly++;
                else if (c == '}' && curly > 0) curly--;
                else if (c == ',' && angle == 0 && round == 0 && square == 0 && curly == 0)
                {
                    yield return list.Substring(start, i - start).Trim();
                    start = i + 1;
                }
            }
            yield return list.Substring(start).Trim();
        }
    }
}
