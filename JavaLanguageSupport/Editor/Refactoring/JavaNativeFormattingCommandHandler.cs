using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json.Linq;

namespace JavaLanguageSupport.Editor.Refactoring
{
    /// <summary>
    /// Pack D3: native VS commands, Java content type only. No custom menus or
    /// key bindings: VS already maps Ctrl+K,D/F/C/U to these editor commands.
    /// Formatting is an asynchronous JDT LS request and changes ONLY the live
    /// editor buffer (never auto-saves). Comments are local and work offline.
    /// </summary>
    [Export(typeof(ICommandHandler))]
    [Name("Java Native Formatting and Comments")]
    [ContentType("java")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class JavaNativeFormattingCommandHandler :
        IChainedCommandHandler<FormatDocumentCommandArgs>,
        IChainedCommandHandler<FormatSelectionCommandArgs>,
        IChainedCommandHandler<CommentSelectionCommandArgs>,
        IChainedCommandHandler<UncommentSelectionCommandArgs>
    {
        [Import] internal ITextDocumentFactoryService Documents = null;
        [Import] internal ITextUndoHistoryRegistry UndoRegistry = null;

        public string DisplayName { get { return "Java: formattazione e commenti nativi"; } }

        private static bool IsJava(ITextView view, ITextBuffer buffer)
        {
            return view != null && !view.IsClosed && buffer != null &&
                ReferenceEquals(view.TextBuffer, buffer) &&
                buffer.ContentType != null && buffer.ContentType.IsOfType("java");
        }

        public CommandState GetCommandState(FormatDocumentCommandArgs args,
            Func<CommandState> nextCommandHandler)
        {
            return IsJava(args.TextView, args.SubjectBuffer) && JavaLspRpc.Connected &&
                Documents != null ? CommandState.Available : nextCommandHandler();
        }

        public CommandState GetCommandState(FormatSelectionCommandArgs args,
            Func<CommandState> nextCommandHandler)
        {
            return IsJava(args.TextView, args.SubjectBuffer) && JavaLspRpc.Connected &&
                Documents != null && !args.TextView.Selection.IsEmpty &&
                args.TextView.Selection.SelectedSpans.Count == 1
                ? CommandState.Available : nextCommandHandler();
        }

        public CommandState GetCommandState(CommentSelectionCommandArgs args,
            Func<CommandState> nextCommandHandler)
        {
            return IsJava(args.TextView, args.SubjectBuffer) &&
                args.TextView.Selection.SelectedSpans.Count <= 1
                ? CommandState.Available : nextCommandHandler();
        }

        public CommandState GetCommandState(UncommentSelectionCommandArgs args,
            Func<CommandState> nextCommandHandler)
        {
            return IsJava(args.TextView, args.SubjectBuffer) &&
                args.TextView.Selection.SelectedSpans.Count <= 1
                ? CommandState.Available : nextCommandHandler();
        }

        public void ExecuteCommand(FormatDocumentCommandArgs args,
            Action nextCommandHandler, CommandExecutionContext executionContext)
        {
            if (!StartFormat(args.TextView, args.SubjectBuffer, false)) nextCommandHandler();
        }

        public void ExecuteCommand(FormatSelectionCommandArgs args,
            Action nextCommandHandler, CommandExecutionContext executionContext)
        {
            if (!StartFormat(args.TextView, args.SubjectBuffer, true)) nextCommandHandler();
        }

        public void ExecuteCommand(CommentSelectionCommandArgs args,
            Action nextCommandHandler, CommandExecutionContext executionContext)
        {
            if (!Comment(args.TextView, args.SubjectBuffer, true)) nextCommandHandler();
        }

        public void ExecuteCommand(UncommentSelectionCommandArgs args,
            Action nextCommandHandler, CommandExecutionContext executionContext)
        {
            if (!Comment(args.TextView, args.SubjectBuffer, false)) nextCommandHandler();
        }

        private bool StartFormat(ITextView view, ITextBuffer buffer, bool selectionOnly)
        {
            if (!IsJava(view, buffer) || !JavaLspRpc.Connected || Documents == null)
                return false;
            ITextDocument document;
            if (!Documents.TryGetTextDocument(buffer, out document)) return false;
            string uri = JavaLspRpc.FileUri(document);
            if (string.IsNullOrWhiteSpace(uri)) return false;
            ITextSnapshot snapshot = buffer.CurrentSnapshot;
            int first = 0, last = snapshot.Length;
            if (selectionOnly)
            {
                if (view.Selection.IsEmpty || view.Selection.SelectedSpans.Count != 1)
                    return false;
                SnapshotSpan span = view.Selection.SelectedSpans[0];
                if (!ReferenceEquals(span.Snapshot, snapshot)) return false;
                // Formatting selection acts on the touched LINES, as in VS.
                // If the selection ends at the NEXT line's start, exclude it.
                int inclusiveEnd = span.End.Position;
                if (inclusiveEnd > span.Start.Position && inclusiveEnd > 0 &&
                    snapshot.GetLineFromPosition(inclusiveEnd).Start.Position == inclusiveEnd)
                    inclusiveEnd--;
                var startLine = snapshot.GetLineFromPosition(span.Start.Position);
                var endLine = snapshot.GetLineFromPosition(inclusiveEnd);
                first = startLine.Start.Position;
                last = endLine.EndIncludingLineBreak.Position;
            }
            int tabSize = 4;
            bool insertSpaces = true;
            try
            {
                tabSize = view.Options.GetOptionValue(DefaultOptions.TabSizeOptionId);
                insertSpaces = view.Options.GetOptionValue(DefaultOptions.ConvertTabsToSpacesOptionId);
            }
            catch (ArgumentException) { /* keep Java defaults */ }
            if (tabSize < 1 || tabSize > 32) tabSize = 4;
            var options = new { tabSize, insertSpaces };
            object request = selectionOnly
                ? (object)new {
                    textDocument = new { uri },
                    range = new {
                        start = JavaLspRpc.Position(snapshot, first),
                        end = JavaLspRpc.Position(snapshot, last)
                    },
                    options
                }
                : (object)new { textDocument = new { uri }, options };
            var stop = new CancellationTokenSource();
            EventHandler onClosed = (sender, args) => stop.Cancel();
            view.Closed += onClosed;
            ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                try
                {
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
                    {
                        timeout.CancelAfter(TimeSpan.FromSeconds(20));
                        JToken reply = await JavaLspRpc.RequestAsync(
                            selectionOnly ? "textDocument/rangeFormatting" : "textDocument/formatting",
                            request, timeout.Token).ConfigureAwait(false);
                        timeout.Token.ThrowIfCancellationRequested();
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(timeout.Token);
                        if (view.IsClosed || !ReferenceEquals(buffer.CurrentSnapshot, snapshot))
                        {
                            Trace.WriteLine("[Java D3] Documento modificato durante la formattazione; nessuna modifica applicata.");
                            return;
                        }
                        ApplyFormatEdits(buffer, snapshot, reply, first, last, selectionOnly);
                    }
                }
                catch (OperationCanceledException) { /* closed / timeout */ }
                catch (Exception ex)
                {
                    Trace.WriteLine("[Java D3] Formattazione non applicata: " + ex);
                    // Do not replace LSP errors with a home-grown Java formatter.
                }
                finally
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    view.Closed -= onClosed;
                    stop.Dispose();
                }
            }).FileAndForget("JavaLanguageSupport/NativeFormat");
            return true;
        }

        private sealed class Edit
        {
            internal int Start;
            internal int End;
            internal string Text;
        }

        private static int Offset(ITextSnapshot snapshot, JToken point)
        {
            int line = (int?)point?["line"] ?? -1;
            int character = (int?)point?["character"] ?? -1;
            if (line < 0 || line >= snapshot.LineCount)
                throw new InvalidOperationException("JDT LS ha restituito una riga non valida.");
            var row = snapshot.GetLineFromLineNumber(line);
            if (character < 0 || character > row.Length)
                throw new InvalidOperationException("JDT LS ha restituito una colonna non valida.");
            return row.Start.Position + character;
        }

        private static string PreferredNewline(ITextSnapshot snapshot)
        {
            for (int i = 0; i < snapshot.LineCount; i++)
            {
                var line = snapshot.GetLineFromLineNumber(i);
                if (line.LineBreakLength != 0) return line.GetLineBreakText();
            }
            return Environment.NewLine;
        }

        private static string LineEndings(string text, string newline)
        {
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");
            return newline == "\n" ? text : text.Replace("\n", newline);
        }

        private void ApplyFormatEdits(ITextBuffer buffer, ITextSnapshot snapshot,
            JToken response, int rangeStart, int rangeEnd, bool selected)
        {
            var array = response as JArray;
            if (array == null || array.Count == 0) return;
            if (array.Count > 2000)
                throw new InvalidOperationException("Troppi TextEdit JDT LS per la formattazione.");
            string newline = PreferredNewline(snapshot);
            var edits = new List<Edit>();
            foreach (JToken item in array)
            {
                if (item == null || item["range"] == null ||
                    item["newText"] == null || item["newText"].Type != JTokenType.String)
                    throw new InvalidOperationException("TextEdit di formattazione non valido.");
                int start = Offset(snapshot, item["range"]["start"]);
                int end = Offset(snapshot, item["range"]["end"]);
                if (start > end || (selected && (start < rangeStart || end > rangeEnd)))
                    throw new InvalidOperationException(
                        "JDT LS propone modifiche fuori dalla selezione: operazione annullata.");
                edits.Add(new Edit { Start = start, End = end,
                    Text = LineEndings((string)item["newText"], newline) });
            }
            edits.Sort((a, b) => b.Start != a.Start ? b.Start.CompareTo(a.Start) :
                b.End.CompareTo(a.End));
            int previousStart = snapshot.Length + 1;
            int previousEnd = snapshot.Length + 1;
            foreach (Edit part in edits)
            {
                if (part.End > previousStart ||
                    (part.Start == previousStart && part.End == previousEnd))
                    throw new InvalidOperationException("JDT LS ha restituito TextEdit sovrapposti.");
                if (buffer.IsReadOnly(part.Start))
                    throw new InvalidOperationException("Documento Java di sola lettura.");
                previousStart = part.Start;
                previousEnd = part.End;
            }
            if (!ReferenceEquals(buffer.CurrentSnapshot, snapshot)) return;
            ApplyAtomic(buffer, edits, selected ? "Java: Formatta selezione" :
                "Java: Formatta documento");
        }

        private void ApplyAtomic(ITextBuffer buffer, IList<Edit> edits, string label)
        {
            if (edits.Count == 0) return;
            ITextUndoTransaction undo = null;
            try
            {
                var history = UndoRegistry == null ? null : UndoRegistry.GetHistory(buffer);
                if (history != null) undo = history.CreateTransaction(label);
                using (ITextEdit writer = buffer.CreateEdit())
                {
                    foreach (Edit part in edits)
                        writer.Replace(part.Start, part.End - part.Start, part.Text);
                    writer.Apply();
                }
                if (undo != null) undo.Complete();
            }
            catch
            {
                if (undo != null) undo.Cancel();
                throw;
            }
            finally
            {
                if (undo != null) undo.Dispose();
            }
        }

        private bool Comment(ITextView view, ITextBuffer buffer, bool add)
        {
            if (!IsJava(view, buffer)) return false;
            ITextSnapshot snapshot = buffer.CurrentSnapshot;
            if (view.Selection.SelectedSpans.Count > 1) return false;
            SnapshotSpan selection = view.Selection.IsEmpty
                ? new SnapshotSpan(snapshot, view.Caret.Position.BufferPosition.Position, 0)
                : view.Selection.SelectedSpans[0];
            if (!ReferenceEquals(selection.Snapshot, snapshot)) return false;
            int inclusiveEnd = selection.End.Position;
            if (!selection.IsEmpty && inclusiveEnd > 0 &&
                snapshot.GetLineFromPosition(inclusiveEnd).Start.Position == inclusiveEnd)
                inclusiveEnd--;
            int from = snapshot.GetLineFromPosition(selection.Start.Position).LineNumber;
            int to = snapshot.GetLineFromPosition(inclusiveEnd).LineNumber;
            var edits = new List<Edit>();
            for (int i = from; i <= to; i++)
            {
                var line = snapshot.GetLineFromLineNumber(i);
                string content = line.GetText();
                int leading = 0;
                while (leading < content.Length &&
                    (content[leading] == ' ' || content[leading] == '\t')) leading++;
                if (leading == content.Length) continue; // blank lines unchanged
                int offset = line.Start.Position + leading;
                if (add)
                {
                    edits.Add(new Edit { Start = offset, End = offset, Text = "// " });
                }
                else if (content.Length - leading >= 2 &&
                    content[leading] == '/' && content[leading + 1] == '/')
                {
                    int count = content.Length - leading >= 3 &&
                        content[leading + 2] == ' ' ? 3 : 2;
                    edits.Add(new Edit { Start = offset, End = offset + count, Text = "" });
                }
            }
            if (edits.Count != 0)
            {
                try
                {
                    // Preserve the editor selection after inserting // prefixes.
                    // The tracking span follows line edits without changing text.
                    ITrackingSpan tracking = selection.IsEmpty ? null :
                        snapshot.CreateTrackingSpan(selection.Span, SpanTrackingMode.EdgeInclusive);
                    ApplyAtomic(buffer, edits,
                        add ? "Java: Commenta selezione" : "Java: Decommenta selezione");
                    if (tracking != null && !view.IsClosed)
                        view.Selection.Select(tracking.GetSpan(buffer.CurrentSnapshot),
                            view.Selection.IsReversed);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine("[Java D3] Commento non applicato: " + ex);
                }
            }
            return true; // the Java command is ours even when no changes are needed
        }
    }
}
