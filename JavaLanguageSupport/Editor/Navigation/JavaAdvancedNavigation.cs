using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using JavaLanguageSupport.Editor.Refactoring;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Newtonsoft.Json.Linq;
using Task = System.Threading.Tasks.Task;

namespace JavaLanguageSupport.Editor.Navigation
{
    /// <summary>
    /// Entry point for advanced navigation. Requests reuse the LSP connection
    /// owned by Visual Studio; no second JDT LS process is started.
    /// </summary>
    internal static class JavaAdvancedNavigation
    {
        internal static async Task RunAsync(JavaEditorOperation operation, string uri,
            ITextSnapshot snapshot, int caret, Action<string> log, CancellationToken token)
        {
            if (operation == JavaEditorOperation.PeekDefinition)
            {
                await JavaPeekDefinition.RunAsync(uri, snapshot, caret, log, token);
                return;
            }
            if (operation == JavaEditorOperation.CallHierarchy)
            {
                await JavaHierarchyWindow.RunAsync(JavaHierarchyKind.Call, uri, snapshot, caret, log, token);
                return;
            }
            if (operation == JavaEditorOperation.TypeHierarchy)
            {
                await JavaHierarchyWindow.RunAsync(JavaHierarchyKind.Type, uri, snapshot, caret, log, token);
                return;
            }
        }

        internal static object PositionArguments(string uri, ITextSnapshot snapshot, int caret)
        {
            return new { textDocument = new { uri }, position = JavaLspRpc.Position(snapshot, caret) };
        }

        internal static List<JObject> Objects(JToken response)
        {
            if (response is JArray)
                return ((JArray)response).OfType<JObject>().ToList();
            var one = response as JObject;
            return one == null ? new List<JObject>() : new List<JObject> { one };
        }

        internal static JavaAdvancedLocation Location(JToken value)
        {
            if (value == null || value.Type != JTokenType.Object) return null;
            string uri = (string)value["targetUri"] ?? (string)value["uri"];
            JToken range = value["targetSelectionRange"] ?? value["selectionRange"] ??
                value["targetRange"] ?? value["range"];
            int line = (int?)range?["start"]?["line"] ?? 0;
            int column = (int?)range?["start"]?["character"] ?? 0;
            if (string.IsNullOrWhiteSpace(uri)) return null;
            return new JavaAdvancedLocation { Uri = uri,
                Line = Math.Max(0, line), Column = Math.Max(0, column) };
        }

        internal static string DisplayName(JToken item)
        {
            string name = (string)item?["name"];
            if (string.IsNullOrWhiteSpace(name))
            {
                var location = Location(item);
                Uri parsed;
                if (location != null && Uri.TryCreate(location.Uri, UriKind.Absolute, out parsed))
                    name = Path.GetFileNameWithoutExtension(parsed.AbsolutePath);
            }
            return string.IsNullOrWhiteSpace(name) ? "Simbolo Java" : name;
        }

        internal static string Identity(JToken item)
        {
            var location = Location(item);
            return location == null ? DisplayName(item) :
                location.Uri + ":" + location.Line + ":" + location.Column + ":" + DisplayName(item);
        }

        internal static async Task OpenAsync(JavaAdvancedLocation location,
            Action<string> log, CancellationToken token)
        {
            if (location == null) return;
            // Also resolves jdt:// URIs to the read-only JDK source cache.
            string path = await JavaNavigation.ResolvePathAsync(location.Uri, log, token);
            if (string.IsNullOrEmpty(path)) return;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            var dte = Package.GetGlobalService(typeof(Microsoft.VisualStudio.Shell.Interop.SDTE)) as DTE;
            if (dte == null)
            {
                log("Servizio DTE di Visual Studio non disponibile.");
                return;
            }
            dte.ItemOperations.OpenFile(path, EnvDTE.Constants.vsViewKindTextView);
            var selected = dte.ActiveDocument == null ? null : dte.ActiveDocument.Selection as TextSelection;
            if (selected != null)
                selected.MoveToLineAndOffset(location.Line + 1, location.Column + 1, false);
        }

        internal static string IdentifierAt(ITextSnapshot snapshot, int caret)
        {
            if (snapshot == null || snapshot.Length == 0) return "tipo";
            int pos = Math.Min(Math.Max(caret, 0), snapshot.Length - 1);
            if (!IsIdentifier(snapshot[pos]) && pos > 0 && IsIdentifier(snapshot[pos - 1])) pos--;
            if (!IsIdentifier(snapshot[pos])) return "tipo";
            int start = pos, end = pos + 1;
            while (start > 0 && IsIdentifier(snapshot[start - 1])) start--;
            while (end < snapshot.Length && IsIdentifier(snapshot[end])) end++;
            return snapshot.GetText(start, end - start);
        }

        private static bool IsIdentifier(char value)
        {
            return char.IsLetterOrDigit(value) || value == '_' || value == '$';
        }
    }

    internal sealed class JavaAdvancedLocation
    {
        internal string Uri;
        internal int Line;
        internal int Column;
    }
}
