using System;
using System.ComponentModel.Composition;
using System.Threading;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using Microsoft.VisualStudio.Text.Editor.Commanding;
using Microsoft.VisualStudio.Utilities;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace JavaLanguageSupport.Editor.Navigation
{
    /// <summary>
    /// Bridges the *native* Visual Studio editor commands to JDT LS.
    /// It is scoped to the java content type: C#/C++ key bindings are untouched.
    /// It does not consume commands when JDT LS is unavailable.
    /// </summary>
    [Export(typeof(ICommandHandler))]
    [Name("Java JDT LS Native Navigation Commands")]
    [ContentType("java")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class JavaNativeNavigationCommandHandler :
        IChainedCommandHandler<GoToDefinitionCommandArgs>,
        IChainedCommandHandler<GoToImplementationCommandArgs>,
        IChainedCommandHandler<FindReferencesCommandArgs>
    {
        [Import] internal ITextDocumentFactoryService Documents = null;

        public string DisplayName { get { return "Java: navigazione e riferimenti nativi"; } }

        public CommandState GetCommandState(GoToDefinitionCommandArgs args,
            Func<CommandState> nextCommandHandler)
        {
            return CanHandle(args.TextView, args.SubjectBuffer) ?
                CommandState.Available : nextCommandHandler();
        }

        public CommandState GetCommandState(GoToImplementationCommandArgs args,
            Func<CommandState> nextCommandHandler)
        {
            return CanHandle(args.TextView, args.SubjectBuffer) ?
                CommandState.Available : nextCommandHandler();
        }

        public CommandState GetCommandState(FindReferencesCommandArgs args,
            Func<CommandState> nextCommandHandler)
        {
            return CanHandle(args.TextView, args.SubjectBuffer) ?
                CommandState.Available : nextCommandHandler();
        }

        public void ExecuteCommand(GoToDefinitionCommandArgs args,
            Action nextCommandHandler, CommandExecutionContext executionContext)
        {
            if (!Launch(args.TextView, args.SubjectBuffer, "definition")) nextCommandHandler();
        }

        public void ExecuteCommand(GoToImplementationCommandArgs args,
            Action nextCommandHandler, CommandExecutionContext executionContext)
        {
            if (!Launch(args.TextView, args.SubjectBuffer, "implementation")) nextCommandHandler();
        }

        public void ExecuteCommand(FindReferencesCommandArgs args,
            Action nextCommandHandler, CommandExecutionContext executionContext)
        {
            if (!Launch(args.TextView, args.SubjectBuffer, "references")) nextCommandHandler();
        }

        private bool CanHandle(ITextView view, ITextBuffer buffer)
        {
            return JavaLspRpc.Connected && Documents != null && view != null && !view.IsClosed &&
                buffer != null && ReferenceEquals(view.TextBuffer, buffer) &&
                buffer.ContentType.IsOfType("java");
        }

        private bool Launch(ITextView view, ITextBuffer buffer, string operation)
        {
            if (!CanHandle(view, buffer)) return false;
            ITextDocument document;
            if (!Documents.TryGetTextDocument(buffer, out document)) return false;
            string uri = JavaLspRpc.FileUri(document);
            if (string.IsNullOrWhiteSpace(uri)) return false;
            ITextSnapshot snapshot = buffer.CurrentSnapshot;
            int caret = Math.Min(view.Caret.Position.BufferPosition.Position, snapshot.Length);
            if (snapshot.Length == 0) return false;

            ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                // Do not allow a delayed JDT LS reply to open a navigation
                // window after the initiating Java editor has been closed.
                using (var lifetime = new CancellationTokenSource())
                {
                    if (view.IsClosed) return;
                    EventHandler onClosed = (sender, args) => lifetime.Cancel();
                    view.Closed += onClosed;
                    Action<string> log = message => System.Diagnostics.Trace.WriteLine(
                        "[Java Native Navigation] " + message);
                    try
                    {
                        if (operation == "references")
                            await JavaReferenceSearch.RunAsync(uri, snapshot, caret, log,
                                lifetime.Token);
                        else
                            await JavaNavigation.ExecuteAsync(
                                operation == "implementation" ?
                                    JavaLanguageSupport.Editor.Refactoring.JavaEditorOperation.GoToImplementation :
                                    JavaLanguageSupport.Editor.Refactoring.JavaEditorOperation.GoToDefinition,
                                uri, snapshot, caret, log, lifetime.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Closing the source view is a normal cancellation, not
                        // an error message that should interrupt the user.
                    }
                    catch (Exception ex)
                    {
                        log(ex.ToString());
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        if (!view.IsClosed)
                            System.Windows.MessageBox.Show("Navigazione Java: " + ex.Message,
                                "Java Language Support", System.Windows.MessageBoxButton.OK,
                                System.Windows.MessageBoxImage.Warning);
                    }
                    finally { view.Closed -= onClosed; }
                }
            }).FileAndForget("JavaLanguageSupport/NativeNavigation");
            return true;
        }
    }
}
