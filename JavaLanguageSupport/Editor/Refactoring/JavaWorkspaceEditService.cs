using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using EnvDTE;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.TextManager.Interop;
using Newtonsoft.Json.Linq;

namespace JavaLanguageSupport.Editor.Refactoring
{
    /// <summary>
    /// Shared, strictly validated WorkspaceEdit executor for Java code actions.
    /// Supports edits in multiple existing local .java files. Resource operations
    /// (rename/create/delete) are deliberately rejected before applying anything;
    /// class-file renaming continues to use the proven JavaInlineRename engine.
    /// </summary>
    internal static class JavaWorkspaceEditService
    {
        private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
        private const int MaximumDocuments = 50;
        private const int MaximumEdits = 5000;

        private sealed class Change
        {
            internal int Start;
            internal int End;
            internal string Text;
        }

        private sealed class DocumentChange
        {
            internal string Path;
            internal ITextBuffer Buffer;
            internal ITextSnapshot Snapshot;
            internal string Before;
            internal List<Change> Edits = new List<Change>();
        }

        private sealed class Plan
        {
            internal string SourcePath;
            internal readonly List<DocumentChange> Documents = new List<DocumentChange>();
            internal int EditCount { get { return Documents.Sum(d => d.Edits.Count); } }
        }

        /// <summary>
        /// Call on the VS UI thread. The caller must have captured the origin
        /// snapshot when asking JDT LS for its action. No files are saved here.
        /// </summary>
        internal static void ApplyWithPreview(JToken edit, string originUri,
            ITextBuffer originBuffer, ITextSnapshot originSnapshot, Action<string> log)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (originBuffer == null || originSnapshot == null ||
                !ReferenceEquals(originBuffer.CurrentSnapshot, originSnapshot))
                throw new InvalidOperationException(
                    "Il documento Java e' cambiato durante la richiesta LSP. Riprova.");

            var groups = Parse(edit, originUri);
            if (groups.Count == 0 || groups.All(pair => pair.Value.Count == 0))
            {
                if (log != null) log("WorkspaceEdit: nessuna modifica proposta.");
                return;
            }
            var plan = Prepare(groups, originUri, originBuffer, originSnapshot);
            if (plan.Documents.Count > 1 && !ConfirmMultiFile(plan))
            {
                if (log != null) log("WorkspaceEdit multi-file annullato dall'utente.");
                return;
            }
            Apply(plan);
            if (log != null)
                log("WorkspaceEdit: applicate " + plan.EditCount + " modifiche in " +
                    plan.Documents.Count + " documenti Java. Salvare i file modificati.");
        }

        private static Dictionary<string, List<JToken>> Parse(JToken edit, string originUri)
        {
            var groups = new Dictionary<string, List<JToken>>(Paths);
            if (edit is JArray)
            {
                if (((JArray)edit).Count > MaximumEdits)
                    throw new NotSupportedException("Troppe modifiche per un singolo documento.");
                Add(groups, JavaPath(originUri), edit as JArray);
                return groups;
            }
            var obj = edit as JObject;
            if (obj == null)
                throw new NotSupportedException("WorkspaceEdit non valido: atteso oggetto o TextEdit[].");

            var documentChanges = obj["documentChanges"] as JArray;
            var changes = obj["changes"] as JObject;
            if (documentChanges != null && changes != null)
                throw new NotSupportedException("WorkspaceEdit con changes e documentChanges simultanei non supportato.");
            if (obj["documentChanges"] != null && documentChanges == null)
                throw new InvalidOperationException("documentChanges non e' un array LSP.");
            if (obj["changes"] != null && changes == null)
                throw new InvalidOperationException("changes non e' un oggetto LSP.");

            if (documentChanges != null)
            {
                foreach (JToken item in documentChanges)
                {
                    var document = item as JObject;
                    if (document == null || document["kind"] != null ||
                        document["textDocument"] == null)
                        throw new NotSupportedException(
                            "WorkspaceEdit con create/rename/delete o operazione sconosciuta: " +
                            "nessuna modifica applicata. Le rinomine sono gestite dal comando Rinomina.");
                    var versionedDocument = document["textDocument"];
                    string uri = (string)versionedDocument["uri"];
                    Add(groups, JavaPath(uri), document["edits"] as JArray);
                }
            }
            else if (changes != null)
            {
                foreach (JProperty group in changes.Properties())
                    Add(groups, JavaPath(group.Name), group.Value as JArray);
            }
            else if (obj["range"] != null && obj["newText"] != null)
                Add(groups, JavaPath(originUri), new JArray(obj));
            else
                throw new NotSupportedException("WorkspaceEdit senza changes/documentChanges.");

            if (groups.Count > MaximumDocuments ||
                groups.Sum(group => group.Value.Count) > MaximumEdits)
                throw new NotSupportedException("WorkspaceEdit troppo grande: " +
                    "limite 50 documenti / 5000 modifiche per operazione.");
            return groups;
        }

        private static string JavaPath(string uri)
        {
            Uri parsed;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out parsed) || !parsed.IsFile)
                throw new NotSupportedException("WorkspaceEdit con URI non locale: " + uri);
            var path = Path.GetFullPath(parsed.LocalPath);
            if (!string.Equals(Path.GetExtension(path), ".java", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("WorkspaceEdit modifica un file non Java: " + path);
            return path;
        }

        private static void Add(Dictionary<string, List<JToken>> groups,
            string file, JArray edits)
        {
            if (edits == null) throw new InvalidOperationException(
                "WorkspaceEdit senza un array TextEdit valido: " + file);
            List<JToken> list;
            if (!groups.TryGetValue(file, out list))
                groups[file] = list = new List<JToken>();
            foreach (JToken edit in edits)
            {
                if (!(edit is JObject) || edit["range"] == null || edit["newText"] == null ||
                    edit["range"]["start"] == null || edit["range"]["end"] == null)
                    throw new InvalidOperationException("TextEdit LSP incompleto: " + file);
                list.Add(edit);
            }
        }

        private static Plan Prepare(Dictionary<string, List<JToken>> groups,
            string sourceUri, ITextBuffer source, ITextSnapshot initial)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string sourcePath = JavaPath(sourceUri);
            var plan = new Plan { SourcePath = sourcePath };
            var adapters = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
            var editorAdapters = adapters == null ? null : adapters.GetService<IVsEditorAdaptersFactoryService>();
            if (editorAdapters == null)
                throw new InvalidOperationException("IVsEditorAdaptersFactoryService non disponibile.");
            var dte = Package.GetGlobalService(typeof(SDTE)) as DTE;
            if (dte == null)
                throw new InvalidOperationException("Visual Studio DTE non disponibile.");

            try
            {
                foreach (var group in groups.OrderBy(x => x.Key, Paths))
                {
                    string file = group.Key;
                    if (!File.Exists(file))
                        throw new FileNotFoundException("File Java mancante nel WorkspaceEdit.", file);
                    if (new FileInfo(file).Length > 2 * 1024 * 1024)
                        throw new NotSupportedException("File Java oltre 2 MiB: " + file);
                    ITextBuffer buffer;
                    if (Paths.Equals(file, sourcePath)) buffer = source;
                    else
                    {
                        // Open the actual VS document so dirty buffers are never
                        // overwritten through direct File.WriteAllText.
                        dte.ItemOperations.OpenFile(file, EnvDTE.Constants.vsViewKindTextView);
                        buffer = OpenBuffer(file, editorAdapters);
                    }
                    if (buffer == null || !buffer.ContentType.IsOfType("java"))
                        throw new InvalidOperationException("Buffer Java non disponibile: " + file);
                    ITextSnapshot snapshot = buffer.CurrentSnapshot;
                    if (Paths.Equals(file, sourcePath) && !ReferenceEquals(snapshot, initial))
                        throw new InvalidOperationException("Il file originale e' cambiato dopo la richiesta LSP.");
                    var entry = new DocumentChange { Path = file, Buffer = buffer,
                        Snapshot = snapshot, Before = snapshot.GetText() };
                    foreach (JToken token in group.Value)
                    {
                        var begin = ToOffset(snapshot, token["range"]["start"]);
                        var end = ToOffset(snapshot, token["range"]["end"]);
                        if (begin > end)
                            throw new InvalidOperationException("Intervallo LSP invertito: " + file);
                        entry.Edits.Add(new Change { Start = begin, End = end,
                            Text = (string)token["newText"] });
                    }
                    entry.Edits = entry.Edits.OrderByDescending(x => x.Start)
                        .ThenByDescending(x => x.End).ToList();
                    int previousStart = snapshot.Length + 1;
                    int previousEnd = snapshot.Length + 1;
                    foreach (Change change in entry.Edits)
                    {
                        if (change.End > previousStart ||
                            (change.Start == previousStart && change.End == previousEnd))
                            throw new InvalidOperationException(
                                "TextEdit sovrapposti/ambigui: " + file);
                        previousStart = change.Start;
                        previousEnd = change.End;
                    }
                    if (entry.Edits.Count != 0) plan.Documents.Add(entry);
                }
            }
            finally
            {
                // The current Java document, not an unrelated opened file,
                // remains active after preparing the preview.
                if (File.Exists(sourcePath))
                    dte.ItemOperations.OpenFile(sourcePath, EnvDTE.Constants.vsViewKindTextView);
            }
            return plan;
        }

        private static int ToOffset(ITextSnapshot snapshot, JToken point)
        {
            if (point == null) throw new InvalidOperationException("Posizione LSP mancante.");
            int line = (int?)point["line"] ?? -1;
            int character = (int?)point["character"] ?? -1;
            if (line < 0 || line >= snapshot.LineCount)
                throw new InvalidOperationException("Riga LSP fuori intervallo.");
            var row = snapshot.GetLineFromLineNumber(line);
            if (character < 0 || character > row.Length)
                throw new InvalidOperationException("Colonna LSP fuori intervallo.");
            return row.Start.Position + character;
        }

        private static ITextBuffer OpenBuffer(string path,
            IVsEditorAdaptersFactoryService editorAdapters)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var table = Package.GetGlobalService(typeof(SVsRunningDocumentTable))
                as IVsRunningDocumentTable;
            if (table == null)
                throw new InvalidOperationException("Running Document Table non disponibile.");
            IVsHierarchy hierarchy;
            uint itemId, cookie;
            IntPtr pointer;
            Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(table.FindAndLockDocument(
                (uint)_VSRDTFLAGS.RDT_NoLock, path, out hierarchy, out itemId,
                out pointer, out cookie));
            if (pointer == IntPtr.Zero)
                throw new InvalidOperationException("Documento non trovato nella RDT: " + path);
            try
            {
                var legacy = Marshal.GetObjectForIUnknown(pointer) as IVsTextBuffer;
                if (legacy == null)
                    throw new InvalidOperationException("IVsTextBuffer non disponibile: " + path);
                return editorAdapters.GetDocumentBuffer(legacy) ??
                    editorAdapters.GetDataBuffer(legacy);
            }
            finally { Marshal.Release(pointer); }
        }

        private static bool ConfirmMultiFile(Plan plan)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dialog = new DialogWindow {
                Title = "Java: Anteprima modifiche multi-file",
                Width = 790, Height = 500, MinWidth = 570, MinHeight = 350,
                ResizeMode = ResizeMode.CanResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false
            };
            dialog.SetResourceReference(Control.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            dialog.SetResourceReference(Control.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            var grid = new Grid { Margin = new Thickness(14) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var summary = new TextBlock {
                Text = plan.EditCount + " modifiche in " + plan.Documents.Count +
                    " file Java. Verificare i percorsi; nessun file verra' salvato automaticamente.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            summary.SetResourceReference(TextBlock.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            Grid.SetRow(summary, 0);
            grid.Children.Add(summary);
            var listing = new ListBox { MinHeight = 140 };
            listing.SetResourceReference(Control.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            listing.SetResourceReference(Control.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            listing.SetResourceReference(Control.BorderBrushProperty,
                EnvironmentColors.ToolWindowBorderBrushKey);
            foreach (var document in plan.Documents)
            {
                listing.Items.Add(Path.GetFileName(document.Path) +
                    " — " + document.Edits.Count + " modifiche — " + document.Path);
                foreach (Change change in document.Edits.Take(8))
                    listing.Items.Add("    posizione " + change.Start + ": " +
                        Brief(document.Before.Substring(change.Start,
                            Math.Min(35, change.End - change.Start))) + "  →  " + Brief(change.Text));
                if (document.Edits.Count > 8)
                    listing.Items.Add("    ... altre " + (document.Edits.Count - 8) + " modifiche");
            }
            Grid.SetRow(listing, 1);
            grid.Children.Add(listing);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0) };
            var apply = new Button { Content = "Applica tutte", MinWidth = 110,
                Margin = new Thickness(4, 0, 4, 0), IsDefault = true };
            var cancel = new Button { Content = "Annulla", MinWidth = 92,
                Margin = new Thickness(4, 0, 4, 0), IsCancel = true };
            foreach (Button button in new[] { apply, cancel })
            {
                button.SetResourceReference(Control.BackgroundProperty,
                    EnvironmentColors.ToolWindowBackgroundBrushKey);
                button.SetResourceReference(Control.ForegroundProperty,
                    EnvironmentColors.ToolWindowTextBrushKey);
                button.SetResourceReference(Control.BorderBrushProperty,
                    EnvironmentColors.ToolWindowBorderBrushKey);
                buttons.Children.Add(button);
            }
            apply.Click += (sender, args) => { dialog.DialogResult = true; };
            cancel.Click += (sender, args) => { dialog.DialogResult = false; };
            Grid.SetRow(buttons, 2);
            grid.Children.Add(buttons);
            dialog.Content = grid;
            return dialog.ShowModal() == true;
        }

        private static string Brief(string value)
        {
            value = (value ?? "").Replace("\r", "\\r").Replace("\n", "\\n");
            return value.Length > 65 ? value.Substring(0, 65) + "..." : value;
        }

        private static void Apply(Plan plan)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Validate the entire plan before touching a single text buffer.
            foreach (DocumentChange document in plan.Documents)
                if (!ReferenceEquals(document.Buffer.CurrentSnapshot, document.Snapshot))
                    throw new InvalidOperationException(
                        "Il documento e' cambiato dopo l'anteprima: " + document.Path);
            var changed = new List<DocumentChange>();
            try
            {
                foreach (DocumentChange document in plan.Documents)
                {
                    changed.Add(document); // rollback even if Apply throws halfway
                    using (var transaction = document.Buffer.CreateEdit())
                    {
                        foreach (Change edit in document.Edits)
                            transaction.Replace(edit.Start, edit.End - edit.Start, edit.Text);
                        if (ReferenceEquals(transaction.Apply(), document.Snapshot))
                            throw new InvalidOperationException("Modifica del buffer rifiutata: " + document.Path);
                    }
                }
            }
            catch (Exception failure)
            {
                var errors = new List<string>();
                foreach (DocumentChange document in changed.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (!string.Equals(document.Buffer.CurrentSnapshot.GetText(),
                            document.Before, StringComparison.Ordinal))
                            document.Buffer.Replace(new Microsoft.VisualStudio.Text.Span(
                                0, document.Buffer.CurrentSnapshot.Length), document.Before);
                    }
                    catch (Exception error) { errors.Add(document.Path + ": " + error.Message); }
                }
                if (errors.Count != 0)
                    throw new InvalidOperationException("WorkspaceEdit non riuscito: " +
                        failure.Message + ". RIPRISTINO INCOMPLETO: " + string.Join("; ", errors),
                        failure);
                throw;
            }
        }
    }
}
