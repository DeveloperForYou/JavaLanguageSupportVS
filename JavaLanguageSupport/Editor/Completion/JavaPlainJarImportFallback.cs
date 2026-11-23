using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using JavaLanguageSupport.LanguageServer;

namespace JavaLanguageSupport.Editor.Completion
{
    /// <summary>
    /// Last-resort Ctrl+. import discovery for explicit Plain JavaJarReference items.
    /// Reads only archive central directories; never executes or extracts JAR content.
    /// Does not make a missing JDT LS classpath valid: that is handled by the dependency sync.
    /// </summary>
    internal static class JavaPlainJarImportFallback
    {
        private const int MaxJars = 80;
        private const int MaxArchiveEntries = 50000;
        private const long MaxJarSize = 100L * 1024 * 1024;
        private static readonly Regex JavaName = new Regex(
            @"^[A-Za-z_$][A-Za-z0-9_$]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        internal static Task<IReadOnlyList<string>> FindAsync(
            string documentUri, string simpleName, CancellationToken token)
        {
            return Task.Run<IReadOnlyList<string>>(
                () => Find(documentUri, simpleName, token), token);
        }

        private static IReadOnlyList<string> Find(
            string documentUri, string simpleName, CancellationToken token)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(simpleName) || !JavaName.IsMatch(simpleName))
                return result.ToList();

            Uri uri;
            if (!Uri.TryCreate(documentUri, UriKind.Absolute, out uri) || !uri.IsFile)
                return result.ToList();
            string documentPath;
            try { documentPath = Path.GetFullPath(uri.LocalPath); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException)
            { return result.ToList(); }

            string projectFile = FindOwningProject(documentPath);
            if (projectFile == null) return result.ToList();
            string[] jarPaths = ReadExplicitPlainJars(projectFile);
            string classFile = simpleName + ".class";

            foreach (string path in jarPaths.Take(MaxJars))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists || info.Length == 0 || info.Length > MaxJarSize ||
                        (info.Attributes & FileAttributes.ReparsePoint) != 0)
                        continue;
                    using (var input = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                    using (var archive = new ZipArchive(input, ZipArchiveMode.Read, false))
                    {
                        if (archive.Entries.Count > MaxArchiveEntries) continue;
                        foreach (var entry in archive.Entries)
                        {
                            token.ThrowIfCancellationRequested();
                            string name = entry.FullName.Replace('\\', '/');
                            if (name.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) continue;
                            int slash = name.LastIndexOf('/');
                            if (slash < 1 || !string.Equals(name.Substring(slash + 1), classFile,
                                StringComparison.Ordinal)) continue;
                            string[] segments = name.Substring(0, slash).Split('/');
                            if (segments.All(s => JavaName.IsMatch(s)))
                                result.Add(string.Join(".", segments) + "." + simpleName);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                    ex is InvalidDataException || ex is ArgumentException)
                {
                    // An unrelated damaged/locked JAR must not break Ctrl+.
                }
            }
            return result.OrderBy(s => s, StringComparer.Ordinal).ToList();
        }

        private static string FindOwningProject(string documentPath)
        {
            foreach (string file in JavaCpsWorkspaceContext.LoadedProjects()
                .Where(f => !string.IsNullOrWhiteSpace(f) && File.Exists(f))
                .OrderByDescending(f => Path.GetDirectoryName(Path.GetFullPath(f)).Length))
            {
                string root = Path.GetDirectoryName(Path.GetFullPath(file)).TrimEnd('\\', '/');
                if (documentPath.StartsWith(root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)) return file;
            }
            return null;
        }

        private static string[] ReadExplicitPlainJars(string projectFile)
        {
            try
            {
                var settings = new XmlReaderSettings
                {
                    XmlResolver = null, DtdProcessing = DtdProcessing.Prohibit,
                    MaxCharactersInDocument = 8 * 1024 * 1024
                };
                XDocument document;
                using (var reader = XmlReader.Create(projectFile, settings))
                    document = XDocument.Load(reader);
                if (document.Root == null || document.Root.Name.LocalName != "Project")
                    return new string[0];
                if (!document.Descendants().Any(e => e.Name.LocalName == "JavaBuildSystem" &&
                    string.Equals(e.Value.Trim(), "Plain", StringComparison.OrdinalIgnoreCase)))
                    return new string[0];

                string root = Path.GetDirectoryName(Path.GetFullPath(projectFile));
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var itemGroup in document.Root.Elements().Where(e => e.Name.LocalName == "ItemGroup"))
                foreach (var item in itemGroup.Elements().Where(e => e.Name.LocalName == "JavaJarReference"))
                {
                    string include = (string)item.Attribute("Include");
                    // Only literal, explicitly referenced JARs, never globs or MSBuild expressions.
                    if (string.IsNullOrWhiteSpace(include) ||
                        include.IndexOfAny(new[] { '*', '?', '$', '%', ';' }) >= 0 ||
                        !include.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        string path = Path.GetFullPath(Path.IsPathRooted(include) ? include :
                            Path.Combine(root, include));
                        paths.Add(path);
                    }
                    catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException ||
                        ex is PathTooLongException) { }
                }
                return paths.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).Take(MaxJars).ToArray();
            }
            catch (Exception ex) when (ex is IOException || ex is XmlException ||
                ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                return new string[0];
            }
        }
    }
}
