
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using EnvDTE;
using JavaLanguageSupport.ProjectSystem.Packages;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TemplateWizard;

namespace JavaLanguageSupport.ProjectSystem.Templates.Wizards
{
    /// <summary>
    /// F2G.1: generate one Java template directly inside a chosen package.
    /// The native item-template host is intentionally prevented from creating
    /// a second file in the directory selected before the wizard opened.
    /// The Java CPS glob includes the generated file automatically.
    /// </summary>
    public abstract class JavaTemplateWithPackageWizard : IWizard2
    {
        private readonly string _resource;
        private bool _started;
        private string _createdFile;
        private DTE _dte;

        protected JavaTemplateWithPackageWizard(string templateKind)
        {
            _resource = "JavaTemplate." + templateKind;
        }

        public void RunStarted(object automationObject,
            Dictionary<string, string> replacementsDictionary,
            WizardRunKind runKind, object[] customParams)
        {
            Start(automationObject as DTE, null, VSConstants.VSITEMID_NIL,
                replacementsDictionary, runKind);
        }

        public void RunStarted(object automationObject,
            Dictionary<string, string> replacementsDictionary,
            WizardRunKind runKind, object[] customParams,
            IVsProject project, uint parentItemId)
        {
            Start(automationObject as DTE, project, parentItemId,
                replacementsDictionary, runKind);
        }

        private void Start(DTE dte, IVsProject project, uint parentItemId,
            Dictionary<string, string> replacements, WizardRunKind runKind)
        {
            if (_started || runKind != WizardRunKind.AsNewItem) return;
            _started = true;
            ThreadHelper.ThrowIfNotOnUIThread();
            string projectFile, selectedDirectory;
            if (!TryLocateProject(dte, project, parentItemId,
                out projectFile, out selectedDirectory))
                throw new WizardCancelledException("Progetto .javaproj non disponibile.");
            // F2G1A-NAME-FIX: $fileinputname$ is NOT a documented reserved
            // Visual Studio template parameter. CPS can omit it completely.
            // Use standard keys when present, otherwise ask in the WPF dialog.
            string suggestedName = ReadProposedName(replacements);
            bool specialDescriptors = _resource == "JavaTemplate.JavaEmpty";
            string projectDirectory = Path.GetDirectoryName(projectFile);
            string buildHint = ReadBuildHint(projectFile);
            // F2G2-PACKAGE-IN-ITEM-WIZARD: pass the exact .javaproj
            // to register newly created empty folders as CPS Folder items.
            var dialog = new JavaTemplatePackageSelectDialog(projectFile,
                projectDirectory, selectedDirectory, buildHint,
                suggestedName, specialDescriptors);
            if (dialog.ShowDialog() != true)
                throw new WizardCancelledException();

            string typeName, fileName, error;
            if (!TryValidateJavaName(dialog.SelectedJavaName, specialDescriptors,
                out typeName, out fileName, out error))
                throw new WizardCancelledException(error);

            string targetDirectory = dialog.SelectedDirectory;
            JavaPackageInfo resolved;
            if (string.IsNullOrEmpty(targetDirectory) ||
                !JavaPackageResolver.TryResolveForDirectory(projectDirectory,
                    targetDirectory, out resolved, out error, buildHint))
                throw new WizardCancelledException(error ?? "Package Java non valido.");
            if (!Directory.Exists(targetDirectory))
                throw new WizardCancelledException("La cartella del package non esiste.");

            string targetFile = Path.Combine(targetDirectory, fileName);
            string source = ReadTemplateResource();
            if (string.Equals(fileName, "module-info.java", StringComparison.OrdinalIgnoreCase) &&
                !resolved.IsDefaultPackage)
                throw new WizardCancelledException(
                    "module-info.java deve essere creato nella radice dei sorgenti.");
            string header = string.Equals(fileName, "module-info.java",
                StringComparison.OrdinalIgnoreCase) ? string.Empty :
                resolved.GetDeclarationHeader("\r\n");
            source = source.Replace("$javaPackageHeader$", header)
                .Replace("$safeitemname$", typeName)
                .Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
            byte[] encoded = new UTF8Encoding(false, true).GetBytes(source);
            try
            {
                // CreateNew fails if a concurrent item appeared after the preview.
                using (var stream = new FileStream(targetFile, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None))
                    stream.Write(encoded, 0, encoded.Length);
                _createdFile = targetFile;
                _dte = dte;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                ex is ArgumentException || ex is NotSupportedException)
            {
                throw new WizardCancelledException("Impossibile creare " + fileName + ": " + ex.Message);
            }
        }

        private string ReadTemplateResource()
        {
            var assembly = typeof(JavaTemplateWithPackageWizard).Assembly;
            using (Stream stream = assembly.GetManifestResourceStream(_resource))
            {
                if (stream == null)
                    throw new WizardCancelledException("Risorsa del template Java non trovata: " + _resource);
                using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true))
                    return reader.ReadToEnd();
            }
        }

        // $itemname$ / $safeitemname$ are standard for native item templates;
        // $fileinputname$ was used by the earlier F2G.1 implementation but
        // is not guaranteed to be present (especially in CPS projects).
        private static string ReadProposedName(Dictionary<string, string> replacements)
        {
            if (replacements == null) return string.Empty;
            string[] keys = { "$itemname$", "$fileinputname$",
                "$safeitemrootname$", "$safeitemname$" };
            foreach (string key in keys)
            {
                string value;
                if (replacements.TryGetValue(key, out value) &&
                    !string.IsNullOrWhiteSpace(value) &&
                    !string.Equals(value.Trim(), key, StringComparison.Ordinal))
                    return value.Trim();
            }
            return string.Empty; // User will enter the name in the WPF dialog.
        }

        // Shared by the WPF preview and the file writer: a valid Java type
        // name must match its .java file's base name.
        internal static bool TryValidateJavaName(string entered,
            bool allowSpecialDescriptors, out string typeName,
            out string fileName, out string error)
        {
            typeName = null;
            fileName = null;
            error = null;
            string name = (entered ?? string.Empty).Trim();
            if (name.EndsWith(".java", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 5);
            if (name.Length == 0)
            {
                error = "Inserire il nome della classe o del file Java.";
                return false;
            }
            bool descriptor = allowSpecialDescriptors &&
                (string.Equals(name, "package-info", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(name, "module-info", StringComparison.OrdinalIgnoreCase));
            string invalid;
            if (!descriptor && !JavaPackageResolver.TryValidatePackageName(name, out invalid))
            {
                error = "Nome Java non valido: " + (invalid ?? name);
                return false;
            }
            // A valid package name may contain dots: class names may not.
            if (!descriptor && name.IndexOf('.') >= 0)
            {
                error = "Inserire un nome semplice (es. Customer), senza package.";
                return false;
            }
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                name.IndexOf('\\') >= 0 || name.IndexOf('/') >= 0)
            {
                error = "Il nome del file contiene caratteri non validi.";
                return false;
            }
            typeName = name;
            fileName = name + ".java";
            return true;
        }

        internal static bool TryLocateProject(DTE dte, IVsProject project, uint parentItemId,
            out string projectFile, out string selectedDirectory)
        {
            projectFile = null;
            selectedDirectory = null;
            if (project != null)
            {
                try
                {
                    string root;
                    if (ErrorHandler.Succeeded(project.GetMkDocument(
                            VSConstants.VSITEMID_ROOT, out root)) && TryProject(root, out projectFile))
                    {
                        var projectDir = Path.GetDirectoryName(projectFile);
                        selectedDirectory = projectDir;
                        if (parentItemId == VSConstants.VSITEMID_ROOT)
                            return true;
                        string selected;
                        if (ErrorHandler.Succeeded(project.GetMkDocument(parentItemId, out selected)))
                        {
                            string path;
                            if (TryWithinProject(projectDir, selected, out path))
                            {
                                selectedDirectory = path;
                                return true;
                            }
                        }
                        return true;
                    }
                }
                catch (Exception ex) when (ex is COMException || ex is IOException ||
                    ex is ArgumentException || ex is NotSupportedException ||
                    ex is UnauthorizedAccessException) { }
            }
            if (dte == null) return false;
            try
            {
                SelectedItems selection = dte.SelectedItems;
                if (selection != null && selection.Count == 1)
                {
                    var node = selection.Item(1);
                    ProjectItem item = node == null ? null : node.ProjectItem;
                    Project candidate = item == null ? (node == null ? null : node.Project)
                        : item.ContainingProject;
                    if (candidate != null && TryProject(candidate.FullName, out projectFile))
                    {
                        string projectDir = Path.GetDirectoryName(projectFile);
                        selectedDirectory = projectDir;
                        if (item != null)
                        {
                            string path;
                            try
                            {
                                if (item.FileCount > 0 && TryWithinProject(projectDir,
                                        item.FileNames[1], out path))
                                    selectedDirectory = path;
                            }
                            catch (COMException) { }
                        }
                        return true;
                    }
                }
                var active = dte.ActiveSolutionProjects as object[];
                if (active != null && active.Length == 1)
                {
                    var candidate = active[0] as Project;
                    if (candidate != null && TryProject(candidate.FullName, out projectFile))
                    {
                        selectedDirectory = Path.GetDirectoryName(projectFile);
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is COMException || ex is IOException ||
                ex is ArgumentException || ex is NotSupportedException ||
                ex is UnauthorizedAccessException) { }
            return false;
        }

        private static bool TryProject(string candidate, out string projectFile)
        {
            projectFile = null;
            if (string.IsNullOrWhiteSpace(candidate) ||
                !candidate.EndsWith(".javaproj", StringComparison.OrdinalIgnoreCase)) return false;
            string path = Path.GetFullPath(candidate);
            if (!File.Exists(path)) return false;
            projectFile = path;
            return true;
        }

        private static bool TryWithinProject(string projectDirectory, string node,
            out string directory)
        {
            directory = null;
            if (string.IsNullOrWhiteSpace(node)) return false;
            Uri uri;
            if (Uri.TryCreate(node, UriKind.Absolute, out uri) && uri.IsFile)
                node = uri.LocalPath;
            string path = Path.GetFullPath(Path.IsPathRooted(node) ? node :
                Path.Combine(projectDirectory, node));
            if (File.Exists(path)) path = Path.GetDirectoryName(path);
            if (!Directory.Exists(path)) return false;
            var project = projectDirectory.TrimEnd('\\', '/');
            if (!string.Equals(path.TrimEnd('\\', '/'), project, StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith(project + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)) return false;
            directory = path;
            return true;
        }

        internal static string ReadBuildHint(string projectFile)
        {
            try
            {
                var settings = new XmlReaderSettings {
                    XmlResolver = null, DtdProcessing = DtdProcessing.Prohibit
                };
                using (var reader = XmlReader.Create(projectFile, settings))
                {
                    var doc = XDocument.Load(reader);
                    var value = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "JavaBuildSystem");
                    return value == null ? null : value.Value.Trim();
                }
            }
            catch (Exception ex) when (ex is XmlException || ex is IOException ||
                ex is UnauthorizedAccessException) { return null; }
        }

        // The selected package may differ from the folder on which the user
        // invoked Add New Item. Suppress the host's original file completely.
        public bool ShouldAddProjectItem(string filePath) { return false; }
        public void ProjectItemFinishedGenerating(ProjectItem projectItem) { }
        public void ProjectFinishedGenerating(Project project) { }
        public void BeforeOpeningFile(ProjectItem projectItem) { }
        public void RunFinished()
        {
            if (_dte == null || string.IsNullOrWhiteSpace(_createdFile)) return;
            try { _dte.ItemOperations.OpenFile(_createdFile); }
            catch (Exception ex) when (ex is COMException || ex is ArgumentException ||
                ex is IOException || ex is NotSupportedException)
            {
                // File creation succeeded. Opening an editor tab is optional.
            }
        }
    }

    // Explicit types bind each ZIP to its own embedded .java source. This is
    // independent of the host's custom-parameter timing and avoids guessing
    // the template kind from an extracted temp path.
    public sealed class JavaClassPackageWizard : JavaTemplateWithPackageWizard
    { public JavaClassPackageWizard() : base("JavaClass") { } }
    public sealed class JavaAbstractClassPackageWizard : JavaTemplateWithPackageWizard
    { public JavaAbstractClassPackageWizard() : base("JavaAbstractClass") { } }
    public sealed class JavaInterfacePackageWizard : JavaTemplateWithPackageWizard
    { public JavaInterfacePackageWizard() : base("JavaInterface") { } }
    public sealed class JavaEnumPackageWizard : JavaTemplateWithPackageWizard
    { public JavaEnumPackageWizard() : base("JavaEnum") { } }
    public sealed class JavaRecordPackageWizard : JavaTemplateWithPackageWizard
    { public JavaRecordPackageWizard() : base("JavaRecord") { } }
    public sealed class JavaAnnotationPackageWizard : JavaTemplateWithPackageWizard
    { public JavaAnnotationPackageWizard() : base("JavaAnnotation") { } }
    public sealed class JavaEmptyPackageWizard : JavaTemplateWithPackageWizard
    { public JavaEmptyPackageWizard() : base("JavaEmpty") { } }
}
