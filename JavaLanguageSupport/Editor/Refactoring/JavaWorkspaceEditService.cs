using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using EnvDTE;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Operations;
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
        // PACK C2: linked undo, preview and persisted multi-document edits.
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
            internal ITextDocument Document;
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
                log("WorkspaceEdit: applicate e salvate " + plan.EditCount + " modifiche in " +
                    plan.Documents.Count + " documenti Java; Undo/Redo collegati.");
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
            var documentFactory = adapters == null ? null : adapters.GetService<ITextDocumentFactoryService>();
            if (editorAdapters == null || documentFactory == null)
                throw new InvalidOperationException("Servizi buffer/documento Visual Studio non disponibili.");
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
                    ITextDocument textDocument;
                    if (!documentFactory.TryGetTextDocument(buffer, out textDocument) ||
                        !Paths.Equals(Path.GetFullPath(textDocument.FilePath), file))
                        throw new InvalidOperationException("Documento Java non disponibile: " + file);
                    var entry = new DocumentChange { Path = file, Buffer = buffer,
                        Snapshot = snapshot, Before = snapshot.GetText(), Document = textDocument };
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

        // PACK C2: preview is non-destructive. The WorkspaceEdit remains atomic:
        // no partial selection of edits that could invalidate the refactoring.
        private sealed class PreviewSelection
        {
            internal DocumentChange Document;
            internal Change Edit;
        }

        private static readonly System.Text.RegularExpressions.Regex PreviewSyntax =
            new System.Text.RegularExpressions.Regex(
                @"//[^\r\n]*|""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|\b(?:package|import|public|private|protected|static|final|abstract|class|interface|enum|record|extends|implements|new|return|void|var|int|long|short|boolean|char|double|float|byte|if|else|for|while|try|catch|throw|throws|this|super|true|false|null)\b|\b[A-Z][\w$]*\b|\b\d+(?:\.\d+)?\b|\b[a-zA-Z_$][\w$]*(?=\()",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private static void AddPreviewSyntax(Paragraph row, string source, bool dark)
        {
            Brush regular = dark ? (Brush)Brushes.Gainsboro : Brushes.Black;
            Brush keyword = new SolidColorBrush(dark ? Color.FromRgb(0x56, 0x9C, 0xD6) : Color.FromRgb(0, 0, 255));
            Brush type = new SolidColorBrush(dark ? Color.FromRgb(0x4E, 0xC9, 0xB0) : Color.FromRgb(0x2B, 0x91, 0xAF));
            Brush method = new SolidColorBrush(dark ? Color.FromRgb(0xDC, 0xDC, 0xAA) : Color.FromRgb(0x74, 0x53, 0x1F));
            Brush literal = new SolidColorBrush(dark ? Color.FromRgb(0xCE, 0x91, 0x78) : Color.FromRgb(0xA0, 0x37, 0x19));
            Brush comment = new SolidColorBrush(dark ? Color.FromRgb(0x6A, 0x99, 0x55) : Color.FromRgb(0, 0x80, 0));
            int previous = 0;
            foreach (System.Text.RegularExpressions.Match token in PreviewSyntax.Matches(source))
            {
                if (token.Index > previous)
                    row.Inlines.Add(new Run(source.Substring(previous, token.Index - previous)) { Foreground = regular });
                string value = token.Value;
                Brush color = value.StartsWith("//", StringComparison.Ordinal) ? comment :
                    (value[0] == '"' || value[0] == '\'') ? literal :
                    char.IsDigit(value[0]) ? literal :
                    char.IsUpper(value[0]) ? type :
                    token.Index + value.Length < source.Length && source[token.Index + value.Length] == '(' ? method : keyword;
                row.Inlines.Add(new Run(value) { Foreground = color });
                previous = token.Index + value.Length;
            }
            if (previous < source.Length)
                row.Inlines.Add(new Run(source.Substring(previous)) { Foreground = regular });
        }

        private static void RenderPreview(RichTextBox viewer, PreviewSelection chosen)
        {
            if (chosen == null || chosen.Document == null) return;
            var doc = chosen.Document;
            var updated = new System.Text.StringBuilder(doc.Before);
            // Descending offsets: every edit uses the SAME original snapshot.
            foreach (Change change in doc.Edits)
            {
                updated.Remove(change.Start, change.End - change.Start);
                updated.Insert(change.Start, change.Text);
            }
            // Map the LSP offset BEFORE normalizing CRLF: VS snapshots count
            // the CR character too, whereas the preview lines below do not.
            string afterRaw = updated.ToString();
            int target = chosen.Edit == null ? 0 : chosen.Edit.Start;
            if (chosen.Edit != null)
            {
                foreach (Change change in doc.Edits)
                    if (change.Start < chosen.Edit.Start)
                        target += change.Text.Length - (change.End - change.Start);
            }
            target = Math.Max(0, Math.Min(target, afterRaw.Length));
            int rowNumber = 0;
            for (int i = 0; i < target; i++) if (afterRaw[i] == '\n') rowNumber++;
            string[] lines = afterRaw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int first = Math.Max(0, rowNumber - 3);
            int last = Math.Min(lines.Length - 1, Math.Max(rowNumber + 7, first + 10));
            bool dark = JavaLanguageSupport.Editor.Intelligence.JavaSemanticPalette.DarkTheme;
            var flow = new FlowDocument {
                PagePadding = new Thickness(9, 7, 9, 7),
                FontFamily = new FontFamily("Consolas"), FontSize = 12.5,
                Foreground = dark ? Brushes.Gainsboro : Brushes.Black,
                Background = viewer.Background
            };
            for (int n = first; n <= last; n++)
            {
                var line = new Paragraph { Margin = new Thickness(0), LineHeight = 19 };
                line.Inlines.Add(new Run((n + 1).ToString().PadLeft(4) + "  ") {
                    Foreground = dark ? (Brush)Brushes.Gray : Brushes.DimGray });
                AddPreviewSyntax(line, lines[n], dark);
                if (n == rowNumber && chosen.Edit != null)
                    line.Background = new SolidColorBrush(dark ? Color.FromRgb(0x34, 0x4D, 0x62) :
                        Color.FromRgb(0xDD, 0xED, 0xFB));
                flow.Blocks.Add(line);
            }
            viewer.Document = flow;
            viewer.ScrollToHome();
        }

        // Explicit WPF template: the stock button may ignore VS foreground when hovered.
        // Mirrors the verified behavior of the Pack C1 rename preview buttons.
        private static void ThemePreviewButton(Button button)
        {
            bool dark = JavaLanguageSupport.Editor.Intelligence.JavaSemanticPalette.DarkTheme;
            Brush hover = new SolidColorBrush(dark ? Color.FromRgb(0x37, 0x45, 0x55) :
                Color.FromRgb(0xD8, 0xE9, 0xF7));
            Brush pressed = new SolidColorBrush(dark ? Color.FromRgb(0x28, 0x4A, 0x66) :
                Color.FromRgb(0xB9, 0xD8, 0xEF));
            Brush foreground = dark ? Brushes.White :
                (Brush)new SolidColorBrush(Color.FromRgb(0x17, 0x2D, 0x42));
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "ButtonBorder";
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
            border.SetValue(Border.PaddingProperty, new Thickness(12, 5, 12, 5));
            border.SetBinding(Border.BackgroundProperty, new Binding("Background") {
                RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") {
                RelativeSource = RelativeSource.TemplatedParent });
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.Name = "ButtonContent";
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            content.SetBinding(TextElement.ForegroundProperty, new Binding("Foreground") {
                RelativeSource = RelativeSource.TemplatedParent });
            border.AppendChild(content);
            template.VisualTree = border;
            var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            over.Setters.Add(new Setter(Border.BackgroundProperty, hover, "ButtonBorder"));
            over.Setters.Add(new Setter(TextElement.ForegroundProperty, foreground, "ButtonContent"));
            template.Triggers.Add(over);
            var focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
            focus.Setters.Add(new Setter(Border.BackgroundProperty, hover, "ButtonBorder"));
            focus.Setters.Add(new Setter(TextElement.ForegroundProperty, foreground, "ButtonContent"));
            template.Triggers.Add(focus);
            var down = new Trigger { Property = Button.IsPressedProperty, Value = true };
            down.Setters.Add(new Setter(Border.BackgroundProperty, pressed, "ButtonBorder"));
            down.Setters.Add(new Setter(TextElement.ForegroundProperty, foreground, "ButtonContent"));
            template.Triggers.Add(down);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.48));
            template.Triggers.Add(disabled);
            button.Template = template;
        }

        private static bool ConfirmMultiFile(Plan plan)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dialog = new DialogWindow {
                Title = "Java: Anteprima modifiche multi-file",
                Width = 890, Height = 625, MinWidth = 630, MinHeight = 425,
                ResizeMode = ResizeMode.CanResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false
            };
            JavaLanguageSupport.Editor.Navigation.JavaNavigationWindowStyle.SetupWindow(dialog);
            dialog.SetResourceReference(Control.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            dialog.SetResourceReference(Control.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            var layout = new Grid { Margin = new Thickness(10) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 100 });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 115 });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var caption = new TextBlock {
                Text = plan.EditCount + " modifiche in " + plan.Documents.Count +
                    " file Java. Operazione unica con Undo/Redo collegati. " +
                    "Dopo Applica, i file coinvolti verranno salvati, incluse eventuali modifiche preesistenti.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
            };
            caption.SetResourceReference(TextBlock.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            Grid.SetRow(caption, 0);
            layout.Children.Add(caption);
            var tree = new TreeView { BorderThickness = new Thickness(1),
                HorizontalContentAlignment = HorizontalAlignment.Stretch };
            tree.SetResourceReference(Control.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            tree.SetResourceReference(Control.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            tree.SetResourceReference(Control.BorderBrushProperty,
                EnvironmentColors.ToolWindowBorderBrushKey);
            ScrollViewer.SetHorizontalScrollBarVisibility(tree, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(tree, ScrollBarVisibility.Auto);
            foreach (DocumentChange doc in plan.Documents)
            {
                var parent = new TreeViewItem {
                    Header = Path.GetFileName(doc.Path) + " - " + doc.Edits.Count + " modifiche",
                    Tag = new PreviewSelection { Document = doc }, ToolTip = doc.Path,
                    IsExpanded = true };
                foreach (Change change in doc.Edits.OrderBy(x => x.Start))
                {
                    int row = doc.Snapshot.GetLineFromPosition(
                        Math.Min(change.Start, Math.Max(0, doc.Snapshot.Length - 1))).LineNumber + 1;
                    string old = doc.Before.Substring(change.Start, change.End - change.Start);
                    parent.Items.Add(new TreeViewItem {
                        Header = "Riga " + row + ": " + Brief(old) + "  →  " + Brief(change.Text),
                        Tag = new PreviewSelection { Document = doc, Edit = change },
                        ToolTip = "Modifica JDT LS in " + doc.Path });
                }
                tree.Items.Add(parent);
            }
            Grid.SetRow(tree, 1);
            layout.Children.Add(tree);
            var splitter = new GridSplitter { Height = 6,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                ResizeDirection = GridResizeDirection.Rows,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                ShowsPreview = true };
            splitter.SetResourceReference(Control.BackgroundProperty,
                EnvironmentColors.ToolWindowBorderBrushKey);
            Grid.SetRow(splitter, 2);
            layout.Children.Add(splitter);
            var preview = new RichTextBox { IsReadOnly = true, IsDocumentEnabled = false,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            preview.SetResourceReference(Control.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            preview.SetResourceReference(Control.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            preview.SetResourceReference(Control.BorderBrushProperty,
                EnvironmentColors.ToolWindowBorderBrushKey);
            Grid.SetRow(preview, 3);
            layout.Children.Add(preview);
            tree.SelectedItemChanged += (sender, args) => {
                var node = args.NewValue as TreeViewItem;
                var current = node == null ? null : node.Tag as PreviewSelection;
                if (current != null) RenderPreview(preview, current);
            };
            var actions = new StackPanel { Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 9, 0, 0) };
            var apply = new Button { Content = "Applica tutte", MinWidth = 115,
                Margin = new Thickness(4, 0, 4, 0), IsDefault = true };
            var cancel = new Button { Content = "Annulla", MinWidth = 95,
                Margin = new Thickness(4, 0, 4, 0), IsCancel = true };
            foreach (Button button in new[] { apply, cancel })
            {
                button.SetResourceReference(Control.BackgroundProperty,
                    EnvironmentColors.ToolWindowBackgroundBrushKey);
                button.SetResourceReference(Control.ForegroundProperty,
                    EnvironmentColors.ToolWindowTextBrushKey);
                button.SetResourceReference(Control.BorderBrushProperty,
                    EnvironmentColors.ToolWindowBorderBrushKey);
                ThemePreviewButton(button);
                actions.Children.Add(button);
            }
            apply.Click += (sender, args) => { dialog.DialogResult = true; };
            cancel.Click += (sender, args) => { dialog.DialogResult = false; };
            Grid.SetRow(actions, 4);
            layout.Children.Add(actions);
            dialog.Content = layout;
            dialog.ContentRendered += (sender, args) => {
                var first = tree.Items.OfType<TreeViewItem>().FirstOrDefault();
                if (first != null) first.IsSelected = true;
            };
            return dialog.ShowModal() == true;
        }

        private static string Brief(string value)
        {
            value = (value ?? "").Replace("\r", "\\r").Replace("\n", "\\n");
            return value.Length > 65 ? value.Substring(0, 65) + "..." : value;
        }

        // PACK C2: a no-op undo primitive linked to all participating text buffers.
        // Its only purpose is persisting the resulting buffers AFTER VS linked
        // Undo/Redo has finished reversing the text edits.
        private sealed class WorkspaceSaveUndoPrimitive : ITextUndoPrimitive
        {
            private readonly List<DocumentChange> documents;
            internal WorkspaceSaveUndoPrimitive(IEnumerable<DocumentChange> items)
            {
                documents = items.ToList();
            }
            public ITextUndoTransaction Parent { get; set; }
            public bool CanUndo { get { return true; } }
            public bool CanRedo { get { return true; } }
            public bool CanMerge(ITextUndoPrimitive other) { return false; }
            public ITextUndoPrimitive Merge(ITextUndoPrimitive other)
            {
                throw new NotSupportedException("WorkspaceEdit: annullamenti non aggregabili.");
            }
            public void Do() { ScheduleSave(); }
            public void Undo() { ScheduleSave(); }
            private void ScheduleSave()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                    new Action(() => {
                        foreach (DocumentChange doc in documents)
                        {
                            try { if (doc.Document.IsDirty) doc.Document.Save(); }
                            catch (Exception ex) {
                                System.Diagnostics.Trace.WriteLine("[Java C2 Undo save] " + doc.Path + ": " + ex);
                            }
                        }
                    }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }

        private static void Apply(Plan plan)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dte = Package.GetGlobalService(typeof(SDTE)) as DTE;
            if (dte == null || dte.UndoContext == null || dte.UndoContext.IsOpen)
                throw new InvalidOperationException(
                    "Undo globale di Visual Studio non disponibile o gia' attivo: nessun file modificato.");
            var model = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
            var registry = model == null ? null : model.GetService<ITextUndoHistoryRegistry>();
            if (registry == null)
                throw new InvalidOperationException("Servizio Undo dei buffer Java assente.");

            // Validate the WHOLE plan before touching any document.
            foreach (DocumentChange doc in plan.Documents)
            {
                if (!ReferenceEquals(doc.Buffer.CurrentSnapshot, doc.Snapshot))
                    throw new InvalidOperationException(
                        "Il documento e' cambiato dopo l'anteprima: " + doc.Path);
                if (!File.Exists(doc.Path) ||
                    (File.GetAttributes(doc.Path) & FileAttributes.ReadOnly) != 0)
                    throw new IOException("File Java mancante o di sola lettura: " + doc.Path);
                if (!Paths.Equals(Path.GetFullPath(doc.Document.FilePath), doc.Path))
                    throw new InvalidOperationException("Il documento aperto ha cambiato percorso: " + doc.Path);
                if (registry.GetHistory(doc.Buffer) == null)
                    throw new InvalidOperationException("Undo Java non disponibile: " + doc.Path);
            }
            var anchor = plan.Documents.FirstOrDefault(x => Paths.Equals(x.Path, plan.SourcePath))
                ?? plan.Documents.First();
            var history = registry.GetHistory(anchor.Buffer);
            var changed = new List<DocumentChange>();
            ITextUndoTransaction saveUndo = null;
            bool linkedOpen = false;
            try
            {
                dte.UndoContext.Open("Java: WorkspaceEdit su " + plan.Documents.Count + " file", true);
                linkedOpen = true;
                saveUndo = history.CreateTransaction("Java: salvataggi collegati Undo/Redo");
                foreach (DocumentChange doc in plan.Documents)
                {
                    changed.Add(doc);
                    using (var transaction = doc.Buffer.CreateEdit())
                    {
                        foreach (Change edit in doc.Edits)
                            transaction.Replace(edit.Start, edit.End - edit.Start, edit.Text);
                        if (ReferenceEquals(transaction.Apply(), doc.Snapshot))
                            throw new InvalidOperationException("Modifica del buffer rifiutata: " + doc.Path);
                    }
                }
                saveUndo.AddUndo(new WorkspaceSaveUndoPrimitive(plan.Documents));
                saveUndo.Complete();
                saveUndo.Dispose();
                saveUndo = null;
                dte.UndoContext.Close();
                linkedOpen = false;
            }
            catch (Exception failure)
            {
                var errors = new List<string>();
                if (saveUndo != null)
                {
                    try { saveUndo.Cancel(); saveUndo.Dispose(); }
                    catch (Exception rollback) { errors.Add("Transazione Undo: " + rollback.Message); }
                }
                if (linkedOpen)
                {
                    try { dte.UndoContext.SetAborted(); dte.UndoContext.Close(); }
                    catch (Exception rollback) { errors.Add("Undo collegato: " + rollback.Message); }
                }
                foreach (DocumentChange doc in changed.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (!string.Equals(doc.Buffer.CurrentSnapshot.GetText(),
                            doc.Before, StringComparison.Ordinal))
                            doc.Buffer.Replace(new Microsoft.VisualStudio.Text.Span(
                                0, doc.Buffer.CurrentSnapshot.Length), doc.Before);
                    }
                    catch (Exception rollback) { errors.Add(doc.Path + ": " + rollback.Message); }
                }
                if (errors.Count != 0)
                    throw new InvalidOperationException("WorkspaceEdit non riuscito: " + failure.Message +
                        ". RIPRISTINO INCOMPLETO: " + string.Join("; ", errors), failure);
                throw;
            }

            // The linked transaction is closed before Save(), as verified by
            // the preceding Pack C1 (otherwise Ctrl+Z can lose the undo group).
            var failures = new List<string>();
            foreach (DocumentChange doc in plan.Documents)
            {
                try { if (doc.Document.IsDirty) doc.Document.Save(); }
                catch (Exception ex) { failures.Add(doc.Path + ": " + ex.Message); }
            }
            if (failures.Count > 0)
                throw new IOException("WorkspaceEdit applicato, ma salvataggio INCOMPLETO: " +
                    string.Join(" | ", failures) + ". Undo/Redo rimangono disponibili; verifica i file.");
        }

    }
}
