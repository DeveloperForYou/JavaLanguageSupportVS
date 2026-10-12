using System;
using System.IO;
using System.Text.RegularExpressions;

namespace JavaLanguageSupport.Build
{
    internal enum JavaAction { Build, Run, Test }
    internal enum JavaProjectKind { SingleFile, Maven, Gradle }

    internal sealed class JavaWorkspace
    {
        public JavaProjectKind Kind { get; private set; }
        public string RootFolder { get; private set; }
        public string SourceFile { get; private set; }
        public string MainClass { get; private set; }

        private JavaWorkspace() { }

        public static JavaWorkspace Detect(string activeFile)
        {
            if (string.IsNullOrWhiteSpace(activeFile) || !File.Exists(activeFile))
                throw new FileNotFoundException("File di lavoro non trovato", activeFile);

            string fullFile = Path.GetFullPath(activeFile);
            string folder = Path.GetDirectoryName(fullFile);
            JavaProjectKind? kind = null;
            string root = null;

            // Il progetto Maven/Gradle più vicino prevale: utile per i multiprogetto.
            for (var candidate = new DirectoryInfo(folder); candidate != null; candidate = candidate.Parent)
            {
                string cwd = candidate.FullName;
                if (File.Exists(Path.Combine(cwd, "pom.xml")))
                {
                    kind = JavaProjectKind.Maven;
                    root = cwd;
                    break;
                }
                if (File.Exists(Path.Combine(cwd, "build.gradle")) ||
                    File.Exists(Path.Combine(cwd, "build.gradle.kts")))
                {
                    kind = JavaProjectKind.Gradle;
                    root = cwd;
                    break;
                }
            }

            if (kind == null)
            {
                if (!fullFile.EndsWith(".java", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Nessun pom.xml/build.gradle rilevato: seleziona un file .java.");
                kind = JavaProjectKind.SingleFile;
                root = folder;
            }

            var ws = new JavaWorkspace {
                Kind = kind.Value,
                RootFolder = root,
                SourceFile = fullFile.EndsWith(".java", StringComparison.OrdinalIgnoreCase) ? fullFile : null
            };
            if (ws.SourceFile != null)
            {
                string code = File.ReadAllText(ws.SourceFile);
                Match match = Regex.Match(code, @"(?m)^\s*package\s+([\w.]+)\s*;");
                string cls = Path.GetFileNameWithoutExtension(ws.SourceFile);
                ws.MainClass = match.Success ? match.Groups[1].Value + "." + cls : cls;
            }
            return ws;
        }
    }
}
