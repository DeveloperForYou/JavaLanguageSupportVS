using System;
using System.ComponentModel.Design;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using EnvDTE;
using JavaLanguageSupport.Build;
using JavaLanguageSupport.Editor.Refactoring;
using JavaLanguageSupport.Editor.Navigation;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Editor;
using Task = System.Threading.Tasks.Task;

namespace JavaLanguageSupport.Commands
{
    /// <summary>
    /// Comandi Java in Strumenti. Il debugger Visual Studio NON viene sostituito:
    /// F5/breakpoint Java richiedono un engine AD7/integrazione DAP separata.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [Guid(PackageGuid)]
    public sealed class JavaCommandsPackage : AsyncPackage
    {
        public const string PackageGuid = "75323116-62f6-483f-a9dd-f2a167762e41";
        private static readonly Guid CommandSet = new Guid("a21a445e-8d9b-4296-9906-a829608ee59c");
        private static readonly Guid OutputPaneId = new Guid("d7b54c89-a301-428d-909c-bd3346a020a2");
        private const int BuildId = 0x0100;
        private const int RunId = 0x0101;
        private const int TestId = 0x0102;
        private const int StatusId = 0x0103;

        private const int FormatId = 0x0110;
        private const int FormatSelectionId = 0x0111;
        private const int OrganizeImportsId = 0x0112;
        private const int RenameId = 0x0113;
        
        private const int GoToTypeDefinitionId = 0x011A;
        
        private const int CallHierarchyId = 0x011C;
        private const int TypeHierarchyId = 0x011D;
        private const int ExtractMethodId = 0x011E;
        private const int ExtractVariableId = 0x011F;
        private const int ExtractConstantId = 0x0120;
        private const int InlineVariableId = 0x0121;
        private const int ChangeMethodSignatureId = 0x0122;
        private const int GenerateConstructorId = 0x0123;
        private const int GenerateGettersSettersId = 0x0124;
        private const int OverrideImplementMethodsId = 0x0125;

        protected override async Task InitializeAsync(CancellationToken cancellationToken,
            IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var service = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (service == null)
                return;

            service.AddCommand(new MenuCommand((s, e) => Start(JavaAction.Build),
                new CommandID(CommandSet, BuildId)));
            service.AddCommand(new MenuCommand((s, e) => Start(JavaAction.Run),
                new CommandID(CommandSet, RunId)));
            service.AddCommand(new MenuCommand((s, e) => Start(JavaAction.Test),
                new CommandID(CommandSet, TestId)));
            service.AddCommand(new MenuCommand((s, e) => ShowIntelliSenseStatus(),
                new CommandID(CommandSet, StatusId)));

            service.AddCommand(new MenuCommand((s, e) => StartEditor(JavaEditorOperation.FormatDocument),
                new CommandID(CommandSet, FormatId)));
            service.AddCommand(new MenuCommand((s, e) => StartEditor(JavaEditorOperation.FormatSelection),
                new CommandID(CommandSet, FormatSelectionId)));
            service.AddCommand(new MenuCommand((s, e) => StartEditor(JavaEditorOperation.OrganizeImports),
                new CommandID(CommandSet, OrganizeImportsId)));
            AddJavaContextCommand(service, RenameId, JavaEditorOperation.RenameSymbol);
            
            AddJavaContextCommand(service, GoToTypeDefinitionId, JavaEditorOperation.GoToTypeDefinition);
            
            AddJavaContextCommand(service, CallHierarchyId, JavaEditorOperation.CallHierarchy);
            AddJavaContextCommand(service, TypeHierarchyId, JavaEditorOperation.TypeHierarchy);
            AddJavaContextCommand(service, ExtractMethodId, JavaEditorOperation.ExtractMethod);
            AddJavaContextCommand(service, ExtractVariableId, JavaEditorOperation.ExtractVariable);
            AddJavaContextCommand(service, ExtractConstantId, JavaEditorOperation.ExtractConstant);
            AddJavaContextCommand(service, InlineVariableId, JavaEditorOperation.InlineVariable);
            AddJavaContextCommand(service, ChangeMethodSignatureId, JavaEditorOperation.ChangeMethodSignature);
            AddJavaContextCommand(service, GenerateConstructorId, JavaEditorOperation.GenerateConstructor);
            AddJavaContextCommand(service, GenerateGettersSettersId, JavaEditorOperation.GenerateGettersSetters);
            AddJavaContextCommand(service, OverrideImplementMethodsId, JavaEditorOperation.OverrideImplementMethods);
        }

        // Solo nell'editor Java e soltanto quando il click destro è su un simbolo.
        // Visible whenever a Java editor is active. Do not depend on a recent
        // mouse click: command status is often queried before PreviewMouseRightButtonDown.
        private bool IsActiveJavaEditor()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var manager = GetService(typeof(SVsTextManager)) as IVsTextManager;
                var model = GetService(typeof(SComponentModel)) as IComponentModel;
                if (manager == null || model == null) return false;
                var adapters = model.GetService<IVsEditorAdaptersFactoryService>();
                IVsTextView view;
                if (adapters == null || manager.GetActiveView(1, null, out view) < 0 || view == null)
                    return false;
                var textView = adapters.GetWpfTextView(view);
                return textView != null && !textView.IsClosed &&
                    textView.TextBuffer.ContentType.IsOfType("java");
            }
            catch (Exception) { return false; }
        }

        private void AddJavaContextCommand(OleMenuCommandService service, int commandId,
            JavaEditorOperation operation)
        {
            var menu = new OleMenuCommand((s, e) => StartEditor(operation),
                new CommandID(CommandSet, commandId));
            menu.BeforeQueryStatus += (s, e) =>
            {
                bool visible = IsActiveJavaEditor();
                menu.Visible = visible;
                menu.Enabled = visible && JavaLspRpc.Connected;
            };
            service.AddCommand(menu);
        }

        private void StartEditor(JavaEditorOperation operation)
        {
            JoinableTaskFactory.RunAsync(async delegate
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var output = await GetServiceAsync(typeof(SVsOutputWindow)) as IVsOutputWindow;
                if (output == null) return;
                Guid paneId = OutputPaneId;
                ErrorHandler.ThrowOnFailure(output.CreatePane(ref paneId, "Java Build / Run / Test", 1, 1));
                IVsOutputWindowPane pane;
                ErrorHandler.ThrowOnFailure(output.GetPane(ref paneId, out pane));
                // Keep editor focus: references/navigation must capture the Java caret.
                // The log remains accessible from View > Output.
                Action<string> log = message => pane.OutputStringThreadSafe(message + Environment.NewLine);
                try
                {
                    var manager = await GetServiceAsync(typeof(SVsTextManager)) as IVsTextManager;
                    var model = await GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
                    if (operation == JavaEditorOperation.GoToDefinition ||
                        operation == JavaEditorOperation.FindReferences ||
                        operation == JavaEditorOperation.GoToImplementation ||
                        operation == JavaEditorOperation.GoToTypeDefinition ||
                        operation == JavaEditorOperation.PeekDefinition ||
                        operation == JavaEditorOperation.CallHierarchy ||
                        operation == JavaEditorOperation.TypeHierarchy ||
                        operation == JavaEditorOperation.InlineVariable ||
                        operation == JavaEditorOperation.ChangeMethodSignature ||
                        operation == JavaEditorOperation.GenerateConstructor ||
                        operation == JavaEditorOperation.GenerateGettersSetters ||
                        operation == JavaEditorOperation.OverrideImplementMethods)
                        JavaCtrlClickNavigation.PrepareContextCaret(manager, model);
                    if (operation == JavaEditorOperation.RenameSymbol)
                    {
                        JavaCtrlClickNavigation.PrepareContextCaret(manager, model);
                        JavaInlineRename.Show(manager, model, log);
                    }
                    else await JavaEditorLspOperations.ExecuteAsync(operation, manager, model,
                        log, CancellationToken.None);
                }
                catch (Exception ex) { log("Java editor: " + ex.Message); }
            }).FileAndForget("JavaLanguageSupport/JavaEditor");
        }
        private void ShowIntelliSenseStatus()
        {
            JoinableTaskFactory.RunAsync(async delegate
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var output = await GetServiceAsync(typeof(SVsOutputWindow)) as IVsOutputWindow;
                if (output == null) return;
                Guid paneId = OutputPaneId;
                ErrorHandler.ThrowOnFailure(output.CreatePane(ref paneId, "Java Build / Run / Test", 1, 1));
                IVsOutputWindowPane pane;
                ErrorHandler.ThrowOnFailure(output.GetPane(ref paneId, out pane));
                pane.Activate();
                pane.OutputStringThreadSafe(JavaLanguageStatus.GetReport() + Environment.NewLine);
            }).FileAndForget("JavaLanguageSupport/Status");
        }

        private void Start(JavaAction action)
        {
            // Event handler VS: non bloccare il thread UI durante la compilazione.
            JoinableTaskFactory.RunAsync(async delegate
            {
                await ExecuteAsync(action);
            }).FileAndForget("JavaLanguageSupport/JavaCommand");
        }

        private async Task ExecuteAsync(JavaAction action)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var output = await GetServiceAsync(typeof(SVsOutputWindow)) as IVsOutputWindow;
            if (output == null)
                return;
            Guid paneId = OutputPaneId;
            ErrorHandler.ThrowOnFailure(output.CreatePane(ref paneId, "Java Build / Run / Test", 1, 1));
            IVsOutputWindowPane pane;
            ErrorHandler.ThrowOnFailure(output.GetPane(ref paneId, out pane));
            pane.Activate();
            var logger = new Action<string>(line => pane.OutputStringThreadSafe(line + Environment.NewLine));

            string filePath;
            try
            {
                var dte = await GetServiceAsync(typeof(SDTE)) as DTE;
                if (dte == null || dte.ActiveDocument == null)
                    throw new InvalidOperationException("Apri un file .java, pom.xml o build.gradle nell'editor.");

                // Le compilazioni devono utilizzare i contenuti salvati su disco.
                dte.ActiveDocument.Save();
                filePath = dte.ActiveDocument.FullName;
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                    throw new InvalidOperationException("Il file attivo deve essere salvato sul disco.");
            }
            catch (Exception ex)
            {
                logger("ERRORE: " + ex.Message);
                return;
            }

            try
            {
                JavaWorkspace workspace = JavaWorkspace.Detect(filePath);
                logger("== Java " + action + " ==");
                logger("Workspace: " + workspace.RootFolder + " (" + workspace.Kind + ")");
                var executor = new JavaTaskExecutor(logger);
                int exitCode = await executor.ExecuteAsync(workspace, action, CancellationToken.None);
                logger(exitCode == 0 ? "Operazione completata correttamente." :
                    "Operazione terminata con errore. Codice: " + exitCode);
            }
            catch (Exception ex)
            {
                logger("ERRORE: " + ex.Message);
            }
        }
    }
}
