using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using EnvDTE;
using JavaLanguageSupport.ProjectSystem.Packages;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TemplateWizard;

namespace JavaLanguageSupport.ProjectSystem.Templates.Wizards
{
    /// <summary>F2G.2: native item-template entry points; no placeholder files.</summary>
    public abstract class JavaSpecialItemTemplateWizard : IWizard2
    {
        private readonly JavaSpecialItemKind _kind;
        private bool _started;
        private string _createdFile;
        private DTE _dte;

        protected JavaSpecialItemTemplateWizard(JavaSpecialItemKind kind)
        {
            _kind = kind;
        }

        public void RunStarted(object automationObject,
            Dictionary<string, string> replacementsDictionary,
            WizardRunKind runKind, object[] customParams)
        {
            Start(automationObject as DTE, null, VSConstants.VSITEMID_NIL, runKind);
        }

        public void RunStarted(object automationObject,
            Dictionary<string, string> replacementsDictionary,
            WizardRunKind runKind, object[] customParams,
            IVsProject project, uint parentItemId)
        {
            Start(automationObject as DTE, project, parentItemId, runKind);
        }

        private void Start(DTE dte, IVsProject project, uint parentItemId,
            WizardRunKind runKind)
        {
            if (_started || runKind != WizardRunKind.AsNewItem) return;
            _started = true;
            ThreadHelper.ThrowIfNotOnUIThread();
            string projectFile, selectedDirectory;
            if (!JavaTemplateWithPackageWizard.TryLocateProject(dte, project,
                parentItemId, out projectFile, out selectedDirectory))
                throw new WizardCancelledException("Progetto .javaproj non disponibile.");

            string projectDirectory = Path.GetDirectoryName(projectFile);
            string buildHint = JavaTemplateWithPackageWizard.ReadBuildHint(projectFile);
            var dialog = new JavaSpecialItemDialog(projectDirectory,
                selectedDirectory, buildHint, _kind);
            if (dialog.ShowDialog() != true)
                throw new WizardCancelledException();

            string destination = dialog.SelectedDirectory;
            if (string.IsNullOrWhiteSpace(destination))
                throw new WizardCancelledException("Destinazione non selezionata.");

            if (_kind == JavaSpecialItemKind.ResourceFolder)
            {
                string parent = Path.GetFullPath(projectDirectory).TrimEnd('\\', '/');
                string target = Path.GetFullPath(destination);
                if (!target.StartsWith(parent + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                    throw new WizardCancelledException("Directory risorse esterna al progetto.");
                // Only the two standard resource roots are exposed by the dialog.
                string main = Path.GetFullPath(Path.Combine(parent, "src", "main", "resources"));
                string test = Path.GetFullPath(Path.Combine(parent, "src", "test", "resources"));
                if (!string.Equals(target, main, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(target, test, StringComparison.OrdinalIgnoreCase))
                    throw new WizardCancelledException("Directory risorse non riconosciuta.");
                try
                {
                    Directory.CreateDirectory(target);
                    string error;
                    if (!JavaEmptyPackageProjectItems.TryInclude(projectFile, target, out error))
                        throw new WizardCancelledException(
                            "Cartella creata, ma non registrata nel .javaproj: " + error);
                }
                catch (Exception ex) when (ex is IOException ||
                    ex is UnauthorizedAccessException || ex is ArgumentException ||
                    ex is NotSupportedException)
                {
                    throw new WizardCancelledException("Creazione cartella non riuscita: " + ex.Message);
                }
                return;
            }

            JavaSourceRootInfo sourceRoot;
            if (!JavaSourceRootResolver.TryFindRootForPath(projectDirectory,
                destination, out sourceRoot, buildHint) ||
                !string.Equals(sourceRoot.FullPath.TrimEnd('\\', '/'),
                    destination.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
                sourceRoot.Kind == JavaSourceRootKind.Test)
                throw new WizardCancelledException("module-info.java deve essere nella radice dei sorgenti principali.");
            string moduleName = dialog.ModuleName;
            string invalid = "";
            if (string.IsNullOrWhiteSpace(moduleName) ||
                !JavaPackageResolver.TryValidatePackageName(moduleName, out invalid))
                throw new WizardCancelledException("Nome di modulo non valido: " + (invalid ?? moduleName));
            string moduleFile = Path.Combine(destination, "module-info.java");
            string source = (dialog.IsOpenModule ? "open module " : "module ") + moduleName +
                " {\r\n}\r\n";
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(source);
            try
            {
                // A newly created Maven/Gradle project may have no source tree yet.
                Directory.CreateDirectory(destination);
                using (var stream = new FileStream(moduleFile, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None))
                    stream.Write(bytes, 0, bytes.Length);
                _createdFile = moduleFile;
                _dte = dte;
            }
            catch (Exception ex) when (ex is IOException ||
                ex is UnauthorizedAccessException || ex is ArgumentException ||
                ex is NotSupportedException)
            {
                throw new WizardCancelledException("Creazione module-info.java non riuscita: " + ex.Message);
            }
        }

        // The wizard creates the file or folder itself. Do not let the native
        // item template host generate a placeholder in the selected node.
        public bool ShouldAddProjectItem(string filePath) { return false; }
        public void ProjectItemFinishedGenerating(ProjectItem projectItem) { }
        public void ProjectFinishedGenerating(Project project) { }
        public void BeforeOpeningFile(ProjectItem projectItem) { }
        public void RunFinished()
        {
            if (_dte == null || string.IsNullOrWhiteSpace(_createdFile)) return;
            try { _dte.ItemOperations.OpenFile(_createdFile); }
            catch (Exception) { /* File remains created if the editor cannot open. */ }
        }
    }

    public sealed class JavaModuleItemTemplateWizard : JavaSpecialItemTemplateWizard
    {
        public JavaModuleItemTemplateWizard() : base(JavaSpecialItemKind.Module) { }
    }

    public sealed class JavaResourceFolderItemTemplateWizard : JavaSpecialItemTemplateWizard
    {
        public JavaResourceFolderItemTemplateWizard() : base(JavaSpecialItemKind.ResourceFolder) { }
    }
}
