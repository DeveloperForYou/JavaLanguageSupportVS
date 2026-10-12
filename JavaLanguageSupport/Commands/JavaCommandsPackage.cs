using System;
using System.ComponentModel.Design;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using JavaLanguageSupport.Build;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
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
