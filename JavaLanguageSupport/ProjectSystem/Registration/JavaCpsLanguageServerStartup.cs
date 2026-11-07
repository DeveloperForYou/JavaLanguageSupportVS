using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

namespace JavaLanguageSupport.ProjectSystem.Registration
{
    /// <summary>
    /// Solution-level bridge for CPS .javaproj -> existing JDT LS client.
    /// Uses Visual Studio's broker; never launches its own language server.
    /// </summary>
    internal sealed class JavaCpsLanguageServerStartup : IVsSolutionEvents, IDisposable
    {
        private sealed class JavaClientMetadata : ILanguageClientMetadata
        {
            public string ClientName { get { return "Java Language Support (JDT LS)"; } }
            public IEnumerable<string> ContentTypes { get { return new[] { "java" }; } }
        }

        private readonly IVsSolution solution;
        private readonly IComponentModel componentModel;
        private readonly SemaphoreSlim loadGate = new SemaphoreSlim(1, 1);
        private uint adviseCookie;
        private bool disposed;

        internal JavaCpsLanguageServerStartup(IVsSolution solution, IComponentModel componentModel)
        {
            this.solution = solution;
            this.componentModel = componentModel;
        }

        internal void Start()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposed || adviseCookie != 0 || solution == null) return;
            ErrorHandler.ThrowOnFailure(solution.AdviseSolutionEvents(this, out adviseCookie));
            ScanLoadedProjects();
        }

        private void ScanLoadedProjects()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var candidates = new List<string>();
            Guid anyType = Guid.Empty;
            IEnumHierarchies enumerator;
            int hr = solution.GetProjectEnum(
                (uint)__VSENUMPROJFLAGS.EPF_LOADEDINSOLUTION,
                ref anyType, out enumerator);
            if (ErrorHandler.Succeeded(hr) && enumerator != null)
            {
                var item = new IVsHierarchy[1];
                uint fetched;
                while (enumerator.Next(1, item, out fetched) == VSConstants.S_OK &&
                    fetched == 1)
                {
                    string project = FindJavaProject(item[0]);
                    if (project != null) candidates.Add(project);
                }
            }
            JavaCpsWorkspaceContext.ReplaceProjects(candidates);
            if (candidates.Count != 0)
                QueueLoad(candidates.OrderBy(p => p,
                    StringComparer.OrdinalIgnoreCase).First());
        }

        private static string FindJavaProject(IVsHierarchy hierarchy)
        {
            if (hierarchy == null) return null;
            string path = null;
            try
            {
                object obj;
                if (ErrorHandler.Succeeded(hierarchy.GetProperty(
                    VSConstants.VSITEMID_ROOT,
                    (int)__VSHPROPID.VSHPROPID_ExtObject, out obj)))
                {
                    var dteProject = obj as Project;
                    if (dteProject != null) path = dteProject.FullName;
                }
                if (string.IsNullOrWhiteSpace(path))
                    hierarchy.GetCanonicalName(VSConstants.VSITEMID_ROOT, out path);
                if ((string.IsNullOrWhiteSpace(path) ||
                    !path.EndsWith(".javaproj", StringComparison.OrdinalIgnoreCase)) &&
                    hierarchy is IVsProject)
                    ((IVsProject)hierarchy).GetMkDocument(
                        VSConstants.VSITEMID_ROOT, out path);
                if (string.IsNullOrWhiteSpace(path)) return null;
                Uri address;
                if (Uri.TryCreate(path, UriKind.Absolute, out address) && address.IsFile)
                    path = address.LocalPath;
                if (!path.EndsWith(".javaproj", StringComparison.OrdinalIgnoreCase))
                    return null;
                path = Path.GetFullPath(path);
                return File.Exists(path) ? path : null;
            }
            catch (Exception ex) when (ex is COMException || ex is IOException ||
                ex is InvalidCastException || ex is ArgumentException ||
                ex is NotSupportedException || ex is UnauthorizedAccessException)
            { return null; }
        }

        private void ObserveOpen(IVsHierarchy hierarchy)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string file = FindJavaProject(hierarchy);
            if (file == null) return;
            JavaCpsWorkspaceContext.AddProject(file);
            QueueLoad(file);
        }

        private void ObserveClose(IVsHierarchy hierarchy)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            JavaCpsWorkspaceContext.RemoveProject(FindJavaProject(hierarchy));
        }

        private void QueueLoad(string projectFile)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposed || !JavaCpsWorkspaceContext.ContainsProject(projectFile)) return;
            ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await TryLoadAsync(projectFile);
            }).FileAndForget("JavaLanguageSupport/JavaCpsLanguageServerStartup");
        }

        private async Task TryLoadAsync(string projectFile)
        {
            await loadGate.WaitAsync();
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (disposed || !JavaCpsWorkspaceContext.ContainsProject(projectFile) ||
                    JavaLspRpc.Connected) return;
                var broker = componentModel.GetService<ILanguageClientBroker>();
                var client = componentModel.GetExtensions<ILanguageClient>()
                    .OfType<JavaLanguageClient>().FirstOrDefault();
                if (broker == null || client == null)
                {
                    JavaLanguageStatus.CpsProjectState(
                        "Il servizio di avvio del client Java non è disponibile.");
                    return;
                }
                JavaLanguageStatus.CpsProjectState(
                    "Progetto Java caricato: avvio del language server richiesto.");
                await broker.LoadAsync(new JavaClientMetadata(), client);
            }
            catch (Exception ex)
            {
                JavaLanguageStatus.CpsProjectState(
                    "Impossibile avviare JDT LS dal progetto: " + ex.Message);
            }
            finally { loadGate.Release(); }
        }

        public int OnAfterOpenProject(IVsHierarchy project, int added)
        { ObserveOpen(project); return VSConstants.S_OK; }
        public int OnAfterLoadProject(IVsHierarchy placeholder, IVsHierarchy actual)
        { ObserveOpen(actual); return VSConstants.S_OK; }
        public int OnAfterOpenSolution(object reserved, int newSolution)
        { ScanLoadedProjects(); return VSConstants.S_OK; }
        public int OnBeforeCloseProject(IVsHierarchy project, int removed)
        { ObserveClose(project); return VSConstants.S_OK; }
        public int OnBeforeUnloadProject(IVsHierarchy actual, IVsHierarchy placeholder)
        { ObserveClose(actual); return VSConstants.S_OK; }
        public int OnBeforeCloseSolution(object reserved)
        { JavaCpsWorkspaceContext.Clear(); return VSConstants.S_OK; }
        public int OnAfterCloseSolution(object reserved)
        { JavaCpsWorkspaceContext.Clear(); return VSConstants.S_OK; }
        public int OnQueryCloseProject(IVsHierarchy project, int removed, ref int cancel)
        { return VSConstants.S_OK; }
        public int OnQueryCloseSolution(object reserved, ref int cancel)
        { return VSConstants.S_OK; }
        public int OnQueryUnloadProject(IVsHierarchy actual, ref int cancel)
        { return VSConstants.S_OK; }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (adviseCookie != 0 && ThreadHelper.CheckAccess())
            {
                try { solution.UnadviseSolutionEvents(adviseCookie); }
                catch (COMException) { }
                adviseCookie = 0;
            }
            JavaCpsWorkspaceContext.Clear();
        }
    }
}
