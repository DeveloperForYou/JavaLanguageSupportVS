using System;
using System.ComponentModel.Composition;
using System.IO;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using Microsoft.VisualStudio.Utilities;

namespace JavaLanguageSupport
{
    /// <summary>
    /// Fallback di completamento per l'editor Java di Visual Studio.
    /// Viene eseguito DOPO l'editor: non duplica le chiusure inserite dai provider nativi.
    /// Gestisce {}, (), [] e i commenti /* ... */ e /** ... */.
    /// Non usa JDT LS: il comportamento e' disponibile anche durante l'indicizzazione.
    /// </summary>
    [Export(typeof(ICommandHandler))]
    [Name(nameof(JavaAutoPairCommandHandler))]
    [ContentType("java")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class JavaAutoPairCommandHandler : IChainedCommandHandler<TypeCharCommandArgs>
    {
        public string DisplayName { get { return "Java: completamento delimitatori"; } }

        public CommandState GetCommandState(
            TypeCharCommandArgs args,
            Func<CommandState> nextCommandHandler)
        {
            return nextCommandHandler();
        }

        public void ExecuteCommand(
            TypeCharCommandArgs args,
            Action nextCommandHandler,
            CommandExecutionContext executionContext)
        {
            char typed = args.TypedChar;
            bool relevant = typed == '(' || typed == '[' || typed == '{' || typed == '*';
            bool selected = !args.TextView.Selection.IsEmpty;

            // Il comando base deve inserire prima il carattere digitato.
            nextCommandHandler();

            if (relevant) WriteLog("TypeChar: " + typed);

            if (!relevant || selected ||
                !ReferenceEquals(args.SubjectBuffer, args.TextView.TextBuffer))
                return;

            ITextBuffer buffer = args.SubjectBuffer;
            ITextSnapshot snapshot = buffer.CurrentSnapshot;
            int position = args.TextView.Caret.Position.BufferPosition.Position;
            if (position < 1 || position > snapshot.Length || snapshot[position - 1] != typed)
                return;

            string suffix = null;
            int syntaxPosition = position - 1;

            if (typed == '*' && position >= 2 && snapshot[position - 2] == '/')
            {
                // L'asterisco e' stato digitato subito dopo '/', formando '/*'.
                syntaxPosition = position - 2;
                if (IsCodeAt(snapshot, syntaxPosition))
                    suffix = "*/";
            }
            else if (IsCodeAt(snapshot, syntaxPosition))
            {
                switch (typed)
                {
                    case '(': suffix = ")"; break;
                    case '[': suffix = "]"; break;
                    case '{': suffix = "}"; break;
                }
            }

            if (suffix == null || buffer.IsReadOnly(position))
                return;

            // Se una chiusura e' stata gia' inserita da Visual Studio, non duplicarla.
            if (position + suffix.Length <= snapshot.Length &&
                snapshot.GetText(position, suffix.Length) == suffix)
            {
                WriteLog("Chiusura gia presente: " + suffix);
                return;
            }

            // Evita di creare una seconda chiusura dentro un commento gia' aperto.
            if (suffix == "*/" && HasExistingCommentTerminator(snapshot, position))
                return;

            ITextSnapshot changed = buffer.Insert(position, suffix);
            if (changed.Length != snapshot.Length + suffix.Length)
                return;

            // Riposiziona il cursore tra apertura e chiusura.
            args.TextView.Caret.MoveTo(new SnapshotPoint(changed, position));
            WriteLog("Inserita chiusura: " + suffix);
        }

        private static void WriteLog(string message)
        {
            try
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "JavaLanguageSupportVS", "logs");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "editor-autopairs.log"),
                    DateTime.Now.ToString("O") + " | " + message + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static bool HasExistingCommentTerminator(ITextSnapshot snapshot, int position)
        {
            // Limitato al resto della riga: non interferisce con altri commenti
            // che possono trovarsi nelle righe successive del documento.
            ITextSnapshotLine line = snapshot.GetLineFromPosition(position);
            int length = line.End.Position - position;
            return length >= 2 && snapshot.GetText(position, length).Contains("*/");
        }

        private enum LexState { Code, LineComment, BlockComment, String, Character, TextBlock }

        private static bool IsCodeAt(ITextSnapshot snapshot, int endExclusive)
        {
            LexState state = LexState.Code;
            for (int i = 0; i < endExclusive; i++)
            {
                char c = snapshot[i];
                char next = i + 1 < endExclusive ? snapshot[i + 1] : '\0';

                if (state == LexState.Code)
                {
                    if (c == '/' && next == '/') { state = LexState.LineComment; i++; }
                    else if (c == '/' && next == '*') { state = LexState.BlockComment; i++; }
                    else if (c == '"')
                    {
                        if (i + 2 < endExclusive && snapshot[i + 1] == '"' && snapshot[i + 2] == '"')
                        { state = LexState.TextBlock; i += 2; }
                        else state = LexState.String;
                    }
                    else if (c == '\'') state = LexState.Character;
                }
                else if (state == LexState.LineComment)
                {
                    if (c == '\n' || c == '\r') state = LexState.Code;
                }
                else if (state == LexState.BlockComment)
                {
                    if (c == '*' && next == '/') { state = LexState.Code; i++; }
                }
                else if (state == LexState.TextBlock)
                {
                    if (c == '"' && i + 2 < endExclusive &&
                        snapshot[i + 1] == '"' && snapshot[i + 2] == '"')
                    { state = LexState.Code; i += 2; }
                }
                else // String / Character
                {
                    if (c == '\\') i++; // Salta il carattere con escape.
                    else if ((state == LexState.String && c == '"') ||
                             (state == LexState.Character && c == '\''))
                        state = LexState.Code;
                }
            }
            return state == LexState.Code;
        }
    }
}
