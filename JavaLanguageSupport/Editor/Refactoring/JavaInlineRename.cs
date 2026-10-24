using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using JavaLanguageSupport.Build;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using EnvDTE;
using Task = System.Threading.Tasks.Task;
using WpfWindow = System.Windows.Window;

namespace JavaLanguageSupport.Editor.Refactoring
{
    /// <summary>
    /// Rinomina Java: finestra WPF compatta ancorata al simbolo, tema Visual Studio.
    /// JDT LS determina i riferimenti, con anteprima e possibilita' di rinominare anche il file .java.
    /// Non e' la UI nativa Roslyn con anteprima live durante la digitazione.
    /// </summary>
    internal static class JavaInlineRename
    {
        // Una sola sessione di rinomina inline attiva alla volta.
        private static WpfWindow activeWindow;

        internal static void Show(IVsTextManager manager, IComponentModel model, Action<string> log)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!JavaLspRpc.Connected || manager == null || model == null) return;
            var adapters = model.GetService<IVsEditorAdaptersFactoryService>();
            var documents = model.GetService<ITextDocumentFactoryService>();
            IVsTextView active;
            if (adapters == null || documents == null || manager.GetActiveView(1, null, out active) < 0)
                return;
            var view = adapters.GetWpfTextView(active);
            if (view == null || !view.TextBuffer.ContentType.IsOfType("java")) return;
            ITextDocument document;
            if (!documents.TryGetTextDocument(view.TextBuffer, out document)) return;
            string uri = JavaLspRpc.FileUri(document);
            if (string.IsNullOrWhiteSpace(uri)) return;
            var snapshot = view.TextBuffer.CurrentSnapshot;
            if (snapshot.Length == 0) return;
            int caret = Math.Min(view.Caret.Position.BufferPosition.Position, snapshot.Length - 1);
            if (!Identifier(snapshot[caret]) && caret > 0 && Identifier(snapshot[caret - 1])) caret--;
            if (!Identifier(snapshot[caret])) { Report(log, "Seleziona un identificatore Java."); return; }
            int start = caret, end = caret + 1;
            while (start > 0 && Identifier(snapshot[start - 1])) start--;
            while (end < snapshot.Length && Identifier(snapshot[end])) end++;
            string oldName = snapshot.GetText(start, end - start);
            if (activeWindow != null && activeWindow.IsVisible) { activeWindow.Activate(); return; }

            var input = new TextBox {
                Text = oldName, FontFamily = new FontFamily("Consolas"),
                MinWidth = 265, Padding = new Thickness(5, 2, 5, 2),
                AcceptsReturn = false, AcceptsTab = false
            };
            input.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            input.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var toggle = new Button { Content = "⌃", Width = 26, Padding = new Thickness(1, 0, 1, 0),
                ToolTip = "Mostra opzioni di rinomina", Margin = new Thickness(3, 0, 0, 0) };
            var row = new DockPanel();
            DockPanel.SetDock(toggle, Dock.Right);
            row.Children.Add(toggle);
            row.Children.Add(input);

            var referenceCount = new TextBlock { Text = "Ricerca dei riferimenti Java in corso…",
                Margin = new Thickness(0, 4, 0, 4), TextWrapping = TextWrapping.Wrap };
            var includeComments = new CheckBox { Content = "Includi commenti", IsChecked = false,
                Margin = new Thickness(0, 3, 0, 0),
                ToolTip = "Sostituzione testuale del nome intero nei commenti .java del progetto; verificare l'anteprima." };
            var includeStrings = new CheckBox { Content = "Includi stringhe", IsChecked = false,
                Margin = new Thickness(0, 3, 0, 0),
                ToolTip = "Sostituzione testuale del nome intero nelle stringhe .java del progetto; verificare l'anteprima." };
            // I CheckBox standard possono usare un testo nero anche con tema VS scuro.
            // Imposta il pennello del tema sia sul controllo sia sulla sua etichetta.
            includeComments.SetResourceReference(Control.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            includeStrings.SetResourceReference(Control.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            var commentsLabel = new TextBlock { Text = "Includi commenti" };
            var stringsLabel = new TextBlock { Text = "Includi stringhe" };
            commentsLabel.SetResourceReference(TextBlock.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            stringsLabel.SetResourceReference(TextBlock.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            includeComments.Content = commentsLabel;
            includeStrings.Content = stringsLabel;
            var hint = new TextBlock { Text = "INVIO per rinominare · MAIUSC+INVIO per l'anteprima · ESC per annullare",
                Margin = new Thickness(0, 6, 0, 0), FontStyle = FontStyles.Italic,
                FontSize = 11, TextWrapping = TextWrapping.Wrap };
            var optionStack = new StackPanel { Visibility = Visibility.Visible };
            optionStack.Children.Add(includeComments);
            optionStack.Children.Add(includeStrings);
            optionStack.Children.Add(hint);
            toggle.Click += (sender, args) => {
                optionStack.Visibility = optionStack.Visibility == Visibility.Visible
                    ? Visibility.Collapsed : Visibility.Visible;
                toggle.Content = optionStack.Visibility == Visibility.Visible ? "⌃" : "⌄";
                input.Focus();
            };
            var panel = new StackPanel { MinWidth = 300, MaxWidth = 510 };
            panel.Children.Add(row);
            panel.Children.Add(referenceCount);
            panel.Children.Add(optionStack);
            var border = new Border { Child = panel, Padding = new Thickness(5),
                BorderThickness = new Thickness(1) };
            border.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            border.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            referenceCount.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            hint.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

            var line = view.TextViewLines.GetTextViewLineContainingBufferPosition(new SnapshotPoint(snapshot, caret));
            if (line == null) return;
            var bounds = line.GetCharacterBounds(new SnapshotPoint(snapshot, start));
            var window = new WpfWindow {
                Title = "Rinomina simbolo Java", Content = border, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = true,
                WindowStartupLocation = WindowStartupLocation.Manual,
                SizeToContent = SizeToContent.WidthAndHeight
            };
            window.SetResourceReference(WpfWindow.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            WpfWindow owner = WpfWindow.GetWindow(view.VisualElement);
            if (owner != null && owner != window) window.Owner = owner;
            else {
                var shell = Package.GetGlobalService(typeof(SVsUIShell)) as IVsUIShell;
                IntPtr hwnd;
                if (shell != null && shell.GetDialogOwnerHwnd(out hwnd) >= 0 && hwnd != IntPtr.Zero)
                    new WindowInteropHelper(window).Owner = hwnd;
            }
            var screen = view.VisualElement.PointToScreen(new System.Windows.Point(
                Math.Max(0, bounds.Left - view.ViewportLeft),
                Math.Max(0, bounds.Bottom - view.ViewportTop)));
            var presentation = PresentationSource.FromVisual(view.VisualElement);
            if (presentation != null && presentation.CompositionTarget != null)
                screen = presentation.CompositionTarget.TransformFromDevice.Transform(screen);
            window.Left = screen.X;
            window.Top = screen.Y;

            bool running = false, released = false;
            int counterGeneration = 0;
            // Pack C1: cancel abandoned LSP estimates rather than allowing
            // old names to occupy the gate while the user commits a rename.
            CancellationTokenSource activeEstimate = null;
            Action cancelEstimate = () => {
                if (activeEstimate != null) {
                    activeEstimate.Cancel();
                    activeEstimate = null;
                }
            };
            // La stima e il commit non devono interrogare contemporaneamente
            // JDT LS per lo stesso simbolo. Il commit ha priorita' sulle stime in coda.
            var renameGate = new SemaphoreSlim(1, 1);
            // Il conteggio usa lo stesso WorkspaceEdit LSP della rinomina reale.
            // Le sole textDocument/references non includono necessariamente import,
            // costruttori e renameFile, quindi possono mostrare numeri differenti.
            var counterTimer = new System.Windows.Threading.DispatcherTimer {
                Interval = TimeSpan.FromMilliseconds(450)
            };
            counterTimer.Tick += (sender, args) => {
                counterTimer.Stop();
                if (running || !window.IsVisible || view.IsClosed) return;
                string typedName = input.Text.Trim();
                if (!ValidIdentifier(typedName)) {
                    referenceCount.Text = "Inserisci un identificatore Java valido.";
                    return;
                }
                // Alla prima apertura il nome non e' ancora cambiato: usa un nome
                // provvisorio per interrogare il server senza applicare alcuna modifica.
                string proposedName = typedName == oldName ? oldName + "_RenamePreview" : typedName;
                int requestId = ++counterGeneration;
                cancelEstimate();
                var estimateCancellation = new CancellationTokenSource();
                activeEstimate = estimateCancellation;
                referenceCount.Text = "Calcolo delle modifiche con JDT LS…";
                ThreadHelper.JoinableTaskFactory.RunAsync(async delegate {
                    try {
                        var args2 = new { textDocument = new { uri },
                            position = JavaLspRpc.Position(snapshot, caret), newName = proposedName };
                        await renameGate.WaitAsync(estimateCancellation.Token).ConfigureAwait(false);
                        JToken estimate;
                        try {
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                            // Ignora le stime superate senza intasare il Language Server.
                            if (!window.IsVisible || running || requestId != counterGeneration) return;
                            estimate = await JavaLspRpc.RequestAsync("textDocument/rename",
                                args2, estimateCancellation.Token).ConfigureAwait(false);
                        } finally {
                            renameGate.Release();
                        }
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        if (window.IsVisible && !running && requestId == counterGeneration)
                            referenceCount.Text = JavaProjectRenameEdits.DescribeRename(estimate);
                    } catch (OperationCanceledException) {
                        // Pack C1: a new name or commit has superseded this estimate.
                    } catch (Exception ex) {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        if (window.IsVisible && !running && requestId == counterGeneration)
                            referenceCount.Text = "Impossibile contare le modifiche: " + ex.Message;
                    } finally {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        if (ReferenceEquals(activeEstimate, estimateCancellation))
                            activeEstimate = null;
                        estimateCancellation.Dispose();
                    }
                }).FileAndForget("JavaLanguageSupport/RenameEstimate");
            };
            input.TextChanged += (sender, args) => {
                if (running || !window.IsVisible) return;
                cancelEstimate();
                ++counterGeneration;
                counterTimer.Stop();
                counterTimer.Start();
            };
            bool originalProhibit = view.Options.GetOptionValue<bool>(DefaultTextViewOptions.ViewProhibitUserInputId);
            Action release = () => {
                if (released) return;
                released = true;
                if (!view.IsClosed)
                    view.Options.SetOptionValue(DefaultTextViewOptions.ViewProhibitUserInputId, originalProhibit);
            };
            EventHandler onViewClosed = null;
            onViewClosed = (sender, args) => { if (window.IsVisible) window.Close(); };
            view.Closed += onViewClosed;
            window.Closed += (sender, args) => {
                counterTimer.Stop();
                cancelEstimate();
                ++counterGeneration;
                view.Closed -= onViewClosed;
                if (activeWindow == window) activeWindow = null;
                release();
            };
            Action cancel = () => { if (!running && window.IsVisible) window.Close(); };
            Action<bool> commit = preview => {
                if (running) return;
                string newName = input.Text.Trim();
                if (!ValidIdentifier(newName)) {
                    referenceCount.Text = "Identificatore Java non valido.";
                    input.Focus(); return;
                }
                if (newName == oldName) { cancel(); return; }
                running = true;
                counterTimer.Stop();
                cancelEstimate();
                ++counterGeneration;
                input.IsEnabled = false;
                toggle.IsEnabled = false;
                includeComments.IsEnabled = false;
                includeStrings.IsEnabled = false;
                referenceCount.Text = "Preparazione della rinomina…";
                // La finestra e' modale solo durante l'inserimento del nome.
                // Chiuderla PRIMA che il refactoring apra gli altri file con DTE/RDT:
                // in questo modo l'IDE non rimane disabilitato durante Prepare/Apply.
                window.Close();
                // Non toccare i sorgenti mentre il Language Server calcola il refactoring.
                ThreadHelper.JoinableTaskFactory.RunAsync(async delegate {
                    try {
                        var parameters = new { textDocument = new { uri },
                            position = JavaLspRpc.Position(snapshot, caret), newName };
                        JToken edit;
                        await renameGate.WaitAsync().ConfigureAwait(false);
                        try {
                            edit = await JavaLspRpc.RequestAsync("textDocument/rename",
                                parameters, CancellationToken.None).ConfigureAwait(false);
                        } finally {
                            renameGate.Release();
                        }
                        JToken definition = await JavaLspRpc.RequestAsync(
                             "textDocument/definition",
                             new { textDocument = new { uri },
                                 position = JavaLspRpc.Position(snapshot, caret) },
                             CancellationToken.None).ConfigureAwait(false);
                         string typeDeclarationFile = JavaProjectRenameEdits.TypeFileFromDefinition(
                             definition, oldName);
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        if (view.IsClosed || view.TextBuffer.CurrentSnapshot.Version.VersionNumber !=
                            snapshot.Version.VersionNumber)
                            throw new InvalidOperationException("Il documento e' cambiato: ripeti la rinomina.");
                        var plan = JavaProjectRenameEdits.Prepare(edit, uri, oldName, newName,
                            includeComments.IsChecked == true, includeStrings.IsChecked == true,
                            manager, model, document.TextBuffer, start, typeDeclarationFile);
                        // Il conteggio definitivo proviene dal WorkspaceEdit che
                        // verra' applicato, non dalla risposta di references.
                        if (plan.EditCount == 0 && plan.RenameCount == 0)
                            throw new InvalidOperationException("JDT LS non ha individuato modifiche applicabili.");
                        referenceCount.Text = plan.EditCount + " modifiche in " + plan.FileCount +
                            " file; " + plan.RenameCount + " nomi di file .java da aggiornare.";
                        if ((preview || includeComments.IsChecked == true ||
                            includeStrings.IsChecked == true) &&
                            !JavaProjectRenameEdits.ShowPreview(WpfWindow.GetWindow(view.VisualElement), plan, view)) {
                            referenceCount.Text = "Anteprima annullata: nessun file modificato.";
                            return;
                        }
                        int changed = JavaProjectRenameEdits.Apply(plan, log);
                        Report(log, "Rinominato '" + oldName + "' in '" + newName + "' (" +
                            changed + " file, " + plan.EditCount + " sostituzioni, " +
                            plan.RenameCount + " file rinominati). Salva i documenti modificati.");
                        window.Close();
                    } catch (Exception ex) {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        Report(log, "Rinomina Java: " + ex.Message);
                        referenceCount.Text = "Errore: " + ex.Message;
                        // L'editor e' tornato attivo: la casella gia' chiusa
                        // non puo' mostrare l'errore. Renderlo sempre visibile.
                        System.Windows.MessageBox.Show(
                            "Rinomina Java non riuscita:\n" + ex.Message,
                            "Rinomina Java", MessageBoxButton.OK, MessageBoxImage.Error);
                    } finally {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        if (window.IsVisible) {
                            running = false;
                            input.IsEnabled = true;
                            toggle.IsEnabled = true;
                            includeComments.IsEnabled = true;
                            includeStrings.IsEnabled = true;
                            input.Focus();
                            Keyboard.Focus(input);
                        }
                    }
                }).FileAndForget("JavaLanguageSupport/InlineRename");
            };
            // Rileva MAIUSC anche quando il tasto modificatore non compare
            // in Keyboard.Modifiers nel contesto modale di Visual Studio.
            // Tratta Invio e Maiusc+Invio prima dei controlli WPF interni.
            window.PreviewKeyDown += (sender, e) => {
                Key key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key == Key.Escape) {
                    e.Handled = true;
                    cancel();
                } else if (key == Key.Enter || key == Key.Return) {
                    bool previewRequested =
                        (e.KeyboardDevice != null &&
                            (e.KeyboardDevice.Modifiers & ModifierKeys.Shift) != 0) ||
                        (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ||
                        Keyboard.IsKeyDown(Key.LeftShift) ||
                        Keyboard.IsKeyDown(Key.RightShift);
                    e.Handled = true;
                    commit(previewRequested);
                }
            };
            // Scorciatoia WPF esplicita di riserva, valida anche se la TextBox
            // consuma l'evento prima della normale gestione dei comandi.
            var previewCommand = new RoutedCommand();
            window.CommandBindings.Add(new CommandBinding(previewCommand, (sender, e) => {
                e.Handled = true;
                commit(true);
            }));
            input.InputBindings.Add(new KeyBinding(previewCommand,
                new KeyGesture(Key.Return, ModifierKeys.Shift)));
            window.InputBindings.Add(new KeyBinding(previewCommand,
                new KeyGesture(Key.Return, ModifierKeys.Shift)));
            window.ContentRendered += (sender, args) => {
                window.Activate();
                FocusManager.SetFocusedElement(window, input);
                input.Focus(); Keyboard.Focus(input); input.SelectAll();
                counterTimer.Start();
            };
            // Mostrare come modale garantisce che Invio/Esc vengano instradati
            // alla TextBox e non catturati dai comandi della finestra editor di VS.
            // Il commit chiude la modale PRIMA di eseguire Prepare/Apply.
            // Non impostare ViewProhibitUserInputId: ShowDialog gia' disabilita
            // l'editor e la sua disabilitazione permanente intercetta i tasti.
            try {
                activeWindow = window;
                window.ShowDialog();
            } catch {
                if (activeWindow == window) activeWindow = null;
                release();
                throw;
            }
        }
        private static void Report(Action<string> log, string message)
        {
            log(message);
            ThreadHelper.ThrowIfNotOnUIThread();
            var status = Package.GetGlobalService(typeof(SVsStatusbar)) as IVsStatusbar;
            if (status != null) status.SetText(message);
        }

        private static bool Identifier(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';
        private static bool ValidIdentifier(string text) => !string.IsNullOrWhiteSpace(text) &&
            (char.IsLetter(text[0]) || text[0] == '_' || text[0] == '$') &&
            text.Skip(1).All(Identifier) && !Reserved.Contains(text);
        private static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.Ordinal)
        { "abstract", "assert", "boolean", "break", "byte", "case", "catch", "char", "class",
          "const", "continue", "default", "do", "double", "else", "enum", "extends", "final",
          "finally", "float", "for", "goto", "if", "implements", "import", "instanceof", "int",
          "interface", "long", "native", "new", "package", "private", "protected", "public",
          "return", "short", "static", "strictfp", "super", "switch", "synchronized", "this",
          "throw", "throws", "transient", "try", "void", "volatile", "while", "true", "false", "null" };
    }


    internal static class JavaProjectRenameEdits
    {
        internal sealed class Edit
        {
            internal int Start, End;
            internal string NewText, OldText, Category;
        }
        internal sealed class Target
        {
            internal string Path;
            internal ITextBuffer Buffer;
            internal ITextSnapshot Original;
            internal ITextDocument Document;
            internal List<Edit> Edits = new List<Edit>();
            internal string Before;
        }
        internal sealed class FileRename
        {
            internal string From, To;
        }
        internal sealed class RenamePlan
        {
            internal readonly List<Target> targets = new List<Target>();
            internal readonly List<FileRename> moves = new List<FileRename>();
            internal int EditCount => targets.Sum(x => x.Edits.Count);
            internal int RenameCount => moves.Count;
            internal int FileCount => targets.Select(x => x.Path)
                .Concat(moves.Select(x => x.From))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            internal string Origin;
            internal string Root;
            internal string OldName, NewName;
        }
        private static readonly StringComparer PathCompare = StringComparer.OrdinalIgnoreCase;

        // Resolve file:// class declarations even when the rename starts at a
        // reference in another source. Never infer this solely from a matching name.
        internal static string TypeFileFromDefinition(JToken result, string oldName)
        {
            if (result == null || result.Type == JTokenType.Null) return null;
            IEnumerable<JToken> destinations = result is JArray
                ? (IEnumerable<JToken>)((JArray)result).Children()
                : new JToken[] { result };
            foreach (JToken destination in destinations) {
                string address = (string)destination["targetUri"] ?? (string)destination["uri"];
                Uri uri;
                if (!Uri.TryCreate(address, UriKind.Absolute, out uri) || !uri.IsFile)
                    continue;
                string path = Path.GetFullPath(uri.LocalPath);
                if (!PathCompare.Equals(Path.GetFileName(path), oldName + ".java") ||
                    !File.Exists(path)) continue;
                var point = (destination["targetSelectionRange"] ?? destination["range"] ??
                    destination["targetRange"])?["start"];
                if (point == null) continue;
                try {
                    string code = File.ReadAllText(path);
                    int row = (int?)point["line"] ?? -1;
                    int column = (int?)point["character"] ?? -1;
                    if (row < 0 || column < 0) continue;
                    int start = 0;
                    for (int i = 0; i < row; i++) {
                        int newline = code.IndexOf('\n', start);
                        if (newline < 0) { start = -1; break; }
                        start = newline + 1;
                    }
                    if (start < 0 || start + column > code.Length) continue;
                    int declaration = FindTopLevelTypeName(code, oldName);
                    if (declaration >= 0 && start + column >= declaration &&
                        start + column < declaration + oldName.Length)
                        return path;
                } catch (IOException) { }
                  catch (UnauthorizedAccessException) { }
            }
            return null;
        }

        // Only a top-level Java type declaration qualifies for a matching file
        // rename. Avoid type-like text in comments or string literals.
        private static int FindTopLevelTypeName(string source, string typeName)
        {
            string pattern = @"\b(?:class|interface|enum|record)\s+(?<id>" +
                Regex.Escape(typeName) + @")\b";
            foreach (Match match in Regex.Matches(source, pattern)) {
                int pos = match.Groups["id"].Index;
                if (IsInJavaCode(source, pos) && IsTypeDeclaration(source, pos))
                    return pos;
            }
            return -1;
        }

        private static bool IsInJavaCode(string text, int stop)
        {
            int state = 0; // code, line comment, block comment, string, char, text block
            for (int i = 0; i < stop; i++) {
                char c = text[i];
                char next = i + 1 < stop ? text[i + 1] : '\0';
                if (state == 0) {
                    if (c == '/' && next == '/') { state = 1; i++; }
                    else if (c == '/' && next == '*') { state = 2; i++; }
                    else if (c == '\"') {
                        if (i + 2 < stop && text[i + 1] == '\"' && text[i + 2] == '\"') {
                            state = 5; i += 2;
                        } else state = 3;
                    } else if (c == '\'') state = 4;
                } else if (state == 1) {
                    if (c == '\r' || c == '\n') state = 0;
                } else if (state == 2) {
                    if (c == '*' && next == '/') { state = 0; i++; }
                } else if (state == 5) {
                    if (c == '\"' && i + 2 < stop && text[i + 1] == '\"' && text[i + 2] == '\"') {
                        state = 0; i += 2;
                    }
                } else {
                    if (c == '\\') i++;
                    else if ((state == 3 && c == '\"') || (state == 4 && c == '\'')) state = 0;
                }
            }
            return state == 0;
        }

        // Conta esattamente le TextEdit restituite dalla stessa richiesta usata per
        // applicare la rinomina, invece delle Location di references (non equivalenti).
        internal static string DescribeRename(JToken response)
        {
            if (!(response is JObject edit))
                return "JDT LS non ha restituito modifiche. Controlla lo stato IntelliSense.";
            int edits = 0, renamedFiles = 0;
            var uniqueFiles = new HashSet<string>(PathCompare);
            var changes = edit["documentChanges"] as JArray;
            if (changes != null) {
                foreach (var change in changes) {
                    string kind = (string)change["kind"];
                    if (kind == "rename") {
                        ++renamedFiles;
                        AddDisplayFile(uniqueFiles, (string)change["oldUri"]);
                    } else if (kind == null && change["textDocument"] != null) {
                        var list = change["edits"] as JArray;
                        if (list != null && list.Count != 0) {
                            edits += list.Count;
                            AddDisplayFile(uniqueFiles, (string)change["textDocument"]["uri"]);
                        }
                    } else return "Operazione LSP non supportata; verifica l'anteprima.";
                }
            } else if (edit["changes"] is JObject simpleChanges) {
                foreach (var item in simpleChanges.Properties()) {
                    var list = item.Value as JArray;
                    if (list == null || list.Count == 0) continue;
                    edits += list.Count;
                    AddDisplayFile(uniqueFiles, item.Name);
                }
            }
            if (edits == 0 && renamedFiles == 0)
                return "Nessuna modifica proposta da JDT LS (verifica l'indicizzazione).";
            return edits + " modifiche in " + uniqueFiles.Count + " file" +
                (renamedFiles > 0 ? ", " + renamedFiles + " file .java da rinominare" : "") +
                ". Il conteggio finale include eventuali commenti/stringhe.";
        }

        private static void AddDisplayFile(HashSet<string> target, string uri)
        {
            Uri address;
            if (Uri.TryCreate(uri, UriKind.Absolute, out address) && address.IsFile)
                target.Add(Path.GetFullPath(address.LocalPath));
        }

        internal static RenamePlan Prepare(JToken response, string originUri, string oldName,
            string newName, bool comments, bool strings, IVsTextManager manager,
            IComponentModel model, ITextBuffer originBuffer, int originSymbolStart,
            string definitionTypeFile)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(response is JObject edit))
                throw new InvalidOperationException("JDT LS non ha restituito un WorkspaceEdit valido.");
            Uri originAddress = new Uri(originUri);
            if (!originAddress.IsFile) throw new InvalidOperationException("Il file originale non e' locale.");
            string origin = Path.GetFullPath(originAddress.LocalPath);
            var plan = new RenamePlan { Origin = origin,
                Root = JavaWorkspace.Detect(origin).RootFolder,
                OldName = oldName, NewName = newName };
            var groups = new Dictionary<string, List<JToken>>(PathCompare);
            var requestedMoves = new List<FileRename>();
            // Legge prima tutti i renameFile, poi i TextDocumentEdit, indipendentemente
            // dall'ordine che JDT LS usa in documentChanges.
            var documents = edit["documentChanges"] as JArray;
            if (documents != null)
                foreach (var item in documents) {
                    if (item["kind"] == null) continue;
                    if ((string)item["kind"] != "rename")
                        throw new NotSupportedException("WorkspaceEdit contiene create/delete non supportati.");
                    requestedMoves.Add(new FileRename {
                        From = LocalJavaPath((string)item["oldUri"]),
                        To = LocalJavaPath((string)item["newUri"])
                    });
                }
            if (documents != null)
                foreach (var item in documents) {
                    if (item["kind"] != null) continue;
                    if (item["textDocument"] == null)
                        throw new NotSupportedException("WorkspaceEdit documentChanges sconosciuto.");
                    string path = LocalJavaPath((string)item["textDocument"]["uri"]);
                    var move = requestedMoves.FirstOrDefault(x => PathCompare.Equals(x.To, path));
                    if (move != null) path = move.From;
                    Add(groups, path, item["edits"] as JArray);
                }
            else if (edit["changes"] is JObject changes)
                foreach (var item in changes.Properties())
                    Add(groups, LocalJavaPath(item.Name), item.Value as JArray);

            // Confirmed semantic class rename: class/interface declaration in the
            // original editor, or a matching definition returned by JDT LS.
            // A random identifier whose spelling matches Foo must NEVER rename Foo.java.
            string classFile = null;
            if (PathCompare.Equals(Path.GetFileName(origin), oldName + ".java") &&
                FindTopLevelTypeName(originBuffer.CurrentSnapshot.GetText(), oldName) == originSymbolStart)
                classFile = origin;
            else if (!string.IsNullOrWhiteSpace(definitionTypeFile) &&
                     PathCompare.Equals(Path.GetFileName(definitionTypeFile), oldName + ".java"))
                classFile = Path.GetFullPath(definitionTypeFile);
            // Do not infer a move for a declaration outside this workspace
            // (e.g. a JDK cache or an external Maven dependency).
            if (classFile != null) {
                string rootPrefix = Path.GetFullPath(plan.Root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
                if (!classFile.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                    classFile = null;
            }
            if (classFile != null && !groups.ContainsKey(classFile))
                groups.Add(classFile, new List<JToken>());

            // Anche una rinomina che contiene soltanto renameFile deve aprire il
            // sorgente per aggiornare il percorso del documento in Visual Studio.
            foreach (var move in requestedMoves)
                if (!groups.ContainsKey(move.From))
                    groups.Add(move.From, new List<JToken>());

            var adapters = model.GetService<IVsEditorAdaptersFactoryService>();
            var factory = model.GetService<ITextDocumentFactoryService>();
            var dte = Package.GetGlobalService(typeof(SDTE)) as DTE;
            if (adapters == null || factory == null || dte == null)
                throw new InvalidOperationException("Servizi dell'editor Visual Studio non disponibili.");

            // Opzioni testuali: ricerca nei file Java del progetto; non invia a JDT LS
            // parametri che textDocument/rename NON supporta. Le sostituzioni sono
            // limitate a identificatori interi nei commenti/stringhe (non al codice).
            if (comments || strings)
                foreach (string path in EnumerateProjectJava(plan.Root)) {
                    // Evita di aprire centinaia di file che non contengono il nome.
                    // Nei file gia' presenti nel WorkspaceEdit si legge lo snapshot VS.
                    if (groups.ContainsKey(path)) continue;
                    if (File.ReadAllText(path).Contains(oldName))
                        groups[path] = new List<JToken>();
                }

            foreach (var group in groups) {
                var path = group.Key;
                if (!File.Exists(path))
                    throw new FileNotFoundException("Sorgente Java da rinominare mancante", path);
                // Non usare GetActiveView(): con la casella di rinomina in
                // primo piano non e' garantito che restituisca il file richiesto.
                // Il documento iniziale usa il buffer gia' catturato da Show();
                // gli altri file sono recuperati per percorso dalla RDT di VS.
                ITextBuffer targetBuffer;
                if (PathCompare.Equals(path, origin))
                    targetBuffer = originBuffer;
                else {
                    dte.ItemOperations.OpenFile(path, EnvDTE.Constants.vsViewKindTextView);
                    targetBuffer = GetOpenDocumentBuffer(path, adapters);
                }
                if (targetBuffer == null || !targetBuffer.ContentType.IsOfType("java"))
                    throw new InvalidOperationException("Buffer Java non disponibile: " + path);
                ITextDocument textDocument;
                if (!factory.TryGetTextDocument(targetBuffer, out textDocument))
                    throw new InvalidOperationException("Documento Java non disponibile: " + path);
                if (!PathCompare.Equals(Path.GetFullPath(textDocument.FilePath), path))
                    throw new InvalidOperationException("Visual Studio ha aperto un documento diverso da quello richiesto: " +
                        path + " (attivo: " + textDocument.FilePath + ").");
                var snapshot = targetBuffer.CurrentSnapshot;
                var target = new Target { Path = path, Buffer = targetBuffer,
                    Original = snapshot, Document = textDocument, Before = snapshot.GetText() };
                foreach (var r in group.Value) {
                    if (r["range"] == null)
                        throw new InvalidOperationException("Modifica LSP senza intervallo: " + path);
                    int from = ToOffset(snapshot, r["range"]?["start"]);
                    int to = ToOffset(snapshot, r["range"]?["end"]);
                    if (from > to) throw new InvalidOperationException("Intervallo non valido: " + path);
                    target.Edits.Add(new Edit { Start = from, End = to,
                        OldText = snapshot.GetText(from, to - from),
                        NewText = (string)r["newText"] ?? "", Category = "JDT LS" });
                }
                // Some JDT LS responses omit the declaration edit (e.g. rename
                // requested from a reference outside the class). Apply the missing
                // declaration only when the class has been verified semantically.
                if (classFile != null && PathCompare.Equals(path, classFile)) {
                    int declaration = FindTopLevelTypeName(target.Before, oldName);
                    if (declaration < 0)
                        throw new InvalidOperationException("Dichiarazione del tipo non trovata: " + path);
                    if (!target.Edits.Any(x => x.Start < declaration + oldName.Length &&
                                               x.End > declaration))
                        target.Edits.Add(new Edit { Start = declaration,
                            End = declaration + oldName.Length, OldText = oldName,
                            NewText = newName, Category = "Dichiarazione tipo Java" });
                }
                if (comments || strings)
                    foreach (var candidate in FindTextualMatches(target.Before, oldName, newName,
                        comments, strings))
                        if (!target.Edits.Any(x => candidate.Start < x.End && x.Start < candidate.End))
                            target.Edits.Add(candidate);
                // Pack C1: remove only *identical* text edits. Distinct,
                // overlapping edits remain an error; do not guess their order.
                target.Edits = target.Edits
                    .GroupBy(x => new { x.Start, x.End, x.NewText })
                    .Select(g => g.First())
                    .OrderByDescending(x => x.Start).ToList();
                int previous = snapshot.Length + 1;
                foreach (var r in target.Edits) {
                    if (r.End < r.Start || r.End > previous || r.Start < 0 || r.End > snapshot.Length)
                        throw new InvalidOperationException("Modifiche sovrapposte o non valide: " + path);
                    previous = r.Start;
                }
                if (target.Edits.Count > 0 ||
                    requestedMoves.Any(x => PathCompare.Equals(x.From, path)))
                    plan.targets.Add(target);
            }

            // In assenza di renameFile esplicito dal server, rinomina il sorgente
            // associato alla dichiarazione del tipo (NON un qualsiasi uso del nome).
            foreach (var target in plan.targets) {
                if (!PathCompare.Equals(Path.GetFileName(target.Path), oldName + ".java")) continue;
                bool typeDeclaration = target.Edits.Any(x => x.Category == "JDT LS" &&
                    x.OldText == oldName && x.NewText == newName &&
                    IsTypeDeclaration(target.Before, x.Start));
                if (typeDeclaration && !requestedMoves.Any(x => PathCompare.Equals(x.From, target.Path)))
                    requestedMoves.Add(new FileRename { From = target.Path,
                        To = Path.Combine(Path.GetDirectoryName(target.Path), newName + ".java") });
            }
            if (classFile != null &&
                !requestedMoves.Any(x => PathCompare.Equals(x.From, classFile)))
                requestedMoves.Add(new FileRename { From = classFile,
                    To = Path.Combine(Path.GetDirectoryName(classFile), newName + ".java") });
            var uniqueMoves = new HashSet<string>(PathCompare);
            foreach (var move in requestedMoves) {
                if (!uniqueMoves.Add(move.From))
                    throw new InvalidOperationException("File rinominato piu' volte: " + move.From);
                if (PathCompare.Equals(move.From, move.To) &&
                    !string.Equals(move.From, move.To, StringComparison.Ordinal))
                    throw new NotSupportedException("Rinomina che cambia solo maiuscole/minuscole non supportata in questa versione.");
                if (!File.Exists(move.From) || File.Exists(move.To))
                    throw new IOException("Destinazione esistente o file sorgente mancante: " + move.To);
                if (!PathCompare.Equals(Path.GetDirectoryName(move.From), Path.GetDirectoryName(move.To)))
                    throw new NotSupportedException("Spostamenti di directory/package non previsti: " + move.To);
                if (PathCompare.Equals(move.From, move.To)) continue;
                var source = plan.targets.FirstOrDefault(x => PathCompare.Equals(x.Path, move.From));
                if (source == null)
                    throw new NotSupportedException("JDT LS richiede il rename di un file senza modifica testuale: " + move.From);
                // Un file modificato e non salvato deve comunque poter essere
                // rinominato. Le modifiche restano nel buffer e saranno salvate
                // nel nuovo percorso dopo ITextDocument.Rename.
                plan.moves.Add(move);
            }
            // Riporta in primo piano il file iniziale, se non era uno dei file non esistenti.
            dte.ItemOperations.OpenFile(origin, EnvDTE.Constants.vsViewKindTextView);
            return plan;
        }

        // Recupera il vero buffer di documento tramite la Running Document Table.
        // Il focus della vista attiva non viene utilizzato per individuare il file.
        private static ITextBuffer GetOpenDocumentBuffer(string path,
            IVsEditorAdaptersFactoryService adapters)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var rdt = Package.GetGlobalService(typeof(SVsRunningDocumentTable))
                as IVsRunningDocumentTable;
            if (rdt == null)
                throw new InvalidOperationException("Running Document Table non disponibile.");
            IVsHierarchy hierarchy;
            uint itemId, cookie;
            IntPtr documentData;
            Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(rdt.FindAndLockDocument(
                (uint)_VSRDTFLAGS.RDT_NoLock, path, out hierarchy, out itemId,
                out documentData, out cookie));
            if (documentData == IntPtr.Zero)
                throw new InvalidOperationException("Il file non e' registrato nell'editor: " + path);
            try {
                var legacy = Marshal.GetObjectForIUnknown(documentData) as IVsTextBuffer;
                if (legacy == null)
                    throw new InvalidOperationException("Il file aperto non e' un buffer di testo: " + path);
                return adapters.GetDocumentBuffer(legacy) ?? adapters.GetDataBuffer(legacy);
            } finally {
                Marshal.Release(documentData);
            }
        }

        // Modello della finestra di anteprima. Le caselle sono legate a modifiche
        // reali del WorkspaceEdit: deselezionare una voce la esclude da Apply.
        private sealed class PreviewEntry
        {
            internal PreviewEntry Parent;
            internal Target Target;
            internal Edit Change;
            internal FileRename Move;
            internal CheckBox Check;
            internal TreeViewItem Item;
            internal readonly List<PreviewEntry> Children = new List<PreviewEntry>();
        }

        private static PreviewEntry AddPreviewEntry(ItemsControl container, PreviewEntry parent,
            string title, string icon, Target target, Edit change, FileRename move,
            string tooltip = null)
        {
            var entry = new PreviewEntry { Parent = parent, Target = target,
                Change = change, Move = move };
            // La CheckBox occupa soltanto il suo quadratino. Il testo e l'icona
            // sono FRATELLI della checkbox: un click sul nodo seleziona la riga
            // e aggiorna l'anteprima, senza cambiarne lo stato incluso/escluso.
            var caption = new StackPanel { Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center };
            entry.Check = new CheckBox { IsChecked = true, Width = 18, Height = 18,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = "Includi/escludi: " + (tooltip ?? title) };
            var symbol = new TextBlock { Text = icon, MinWidth = 19,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = true };
            symbol.SetResourceReference(TextBlock.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            var text = new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(TextBlock.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            caption.Children.Add(entry.Check);
            caption.Children.Add(symbol);
            caption.Children.Add(text);

            // Il template WPF/VS cambia il colore di selezione quando TreeView
            // perde il focus (anche solo cliccando sul riquadro del codice).
            // Una superficie opaca dello STESSO tema dietro alle etichette
            // impedisce che il testo chiaro finisca su un fondo chiaro.
            // Il bordo sinistro segnala il nodo selezionato anche senza focus.
            var header = new Border { Child = caption,
                BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(2, 1, 4, 1),
                HorizontalAlignment = HorizontalAlignment.Stretch };
            header.SetResourceReference(Border.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            var headerStyle = new Style(typeof(Border));
            headerStyle.Setters.Add(new Setter(Border.BorderBrushProperty,
                Brushes.Transparent));
            var selectedStyle = new DataTrigger {
                Binding = new Binding("IsSelected") {
                    RelativeSource = new RelativeSource(
                        RelativeSourceMode.FindAncestor, typeof(TreeViewItem), 1)
                },
                Value = true
            };
            selectedStyle.Setters.Add(new Setter(Border.BorderBrushProperty,
                SystemColors.HighlightBrush));
            headerStyle.Triggers.Add(selectedStyle);
            header.Style = headerStyle;

            entry.Item = new TreeViewItem { Header = header, Tag = entry,
                IsExpanded = true, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                ToolTip = tooltip ?? title };
            container.Items.Add(entry.Item);
            if (parent != null) parent.Children.Add(entry);
            return entry;
        }

        private static void SetPreviewChildren(PreviewEntry entry, bool selected)
        {
            entry.Check.IsChecked = selected;
            foreach (var child in entry.Children) SetPreviewChildren(child, selected);
        }

        private static void RefreshPreviewParents(PreviewEntry entry)
        {
            while (entry != null) {
                if (entry.Children.Count > 0) {
                    if (entry.Children.All(x => x.Check.IsChecked == true))
                        entry.Check.IsChecked = true;
                    else if (entry.Children.All(x => x.Check.IsChecked == false))
                        entry.Check.IsChecked = false;
                    else entry.Check.IsChecked = null;
                }
                entry = entry.Parent;
            }
        }

        private static PreviewEntry FirstPreviewLeaf(PreviewEntry entry)
        {
            if (entry.Change != null || entry.Move != null) return entry;
            foreach (var child in entry.Children) {
                var found = FirstPreviewLeaf(child);
                if (found != null) return found;
            }
            return null;
        }

        // Pack C1 Preview/Theme Hotfix 2: show the exact old/new LSP text, not
        // just the resulting source line (which can hide a rename in the tree).
        // Pack C1 Preview/Syntax Hotfix 3: never flatten multiline TextEdits
        // into a single tree row containing literal escaped newline sequences.
        // One checkbox still represents ONE atomic LSP TextEdit.
        private static string PreviewLineText(Target target, Edit change,
            string oldName, string newName)
        {
            int start = Math.Min(change.Start, Math.Max(0, target.Original.Length - 1));
            int end = Math.Min(Math.Max(change.Start, change.End - 1),
                Math.Max(0, target.Original.Length - 1));
            int first = target.Original.GetLineFromPosition(start).LineNumber + 1;
            int last = target.Original.GetLineFromPosition(end).LineNumber + 1;
            string where = first == last ? "Riga " + first :
                "Righe " + first + "-" + last;
            if (string.Equals(change.OldText, change.NewText,
                StringComparison.Ordinal))
                return where + ": (nessuna differenza LSP)";

            string pattern = @"(?<![A-Za-z0-9_$])" + Regex.Escape(oldName) +
                @"(?![A-Za-z0-9_$])";
            int occurrences = Regex.Matches(change.OldText ?? "", pattern).Count;
            if (occurrences > 0 && (change.NewText ?? "").Contains(newName))
                return where + ": " + oldName + " → " + newName +
                    (occurrences > 1 ? " (" + occurrences + " occorrenze)" : "");
            return where + ": modifica Java (vedi anteprima)";
        }

        // Pack C1 Preview/Syntax Hotfix 3: use the SAME Java editor font/color
        // classifications (including Fonts and Colors user overrides) instead
        // of hard-coded guesses. If VS exposes no map, use the editor defaults.
        private sealed class PreviewJavaColors
        {
            internal Brush Plain, Keyword, Type, Method, Variable, Namespace;
            internal Brush Comment, Literal, Number, Bracket, Separator, Highlight;
        }

        private static Brush PreviewColor(IClassificationFormatMap map,
            IClassificationTypeRegistryService registry, string name, Brush fallback)
        {
            if (map == null || registry == null) return fallback;
            try {
                var kind = registry.GetClassificationType(name);
                if (kind == null) return fallback;
                var style = map.GetTextProperties(kind);
                return style != null && !style.ForegroundBrushEmpty &&
                    style.ForegroundBrush != null ? style.ForegroundBrush : fallback;
            } catch (Exception) { return fallback; }
        }

        private static PreviewJavaColors GetPreviewColors(ITextView editor)
        {
            bool dark = JavaLanguageSupport.Editor.Intelligence.JavaSemanticPalette.DarkTheme;
            Brush baseText = dark ? Brushes.Gainsboro : Brushes.Black;
            var colors = new PreviewJavaColors {
                Plain = baseText,
                Keyword = new SolidColorBrush(dark ? Color.FromRgb(0x56, 0x9C, 0xD6) : Color.FromRgb(0, 0, 0xFF)),
                Type = new SolidColorBrush(dark ? Color.FromRgb(0x4E, 0xC9, 0xB0) : Color.FromRgb(0x2B, 0x91, 0xAF)),
                Method = new SolidColorBrush(dark ? Color.FromRgb(0xDC, 0xDC, 0xAA) : Color.FromRgb(0x74, 0x53, 0x1F)),
                Variable = new SolidColorBrush(dark ? Color.FromRgb(0x9C, 0xDC, 0xFE) : Color.FromRgb(0, 0, 0)),
                Namespace = new SolidColorBrush(dark ? Color.FromRgb(0x82, 0xB8, 0xE6) : Color.FromRgb(0, 0, 0xFF)),
                Comment = new SolidColorBrush(dark ? Color.FromRgb(0x6A, 0x99, 0x55) : Color.FromRgb(0, 0x80, 0)),
                Literal = new SolidColorBrush(dark ? Color.FromRgb(0xCE, 0x91, 0x78) : Color.FromRgb(0xA0, 0x37, 0x19)),
                Number = new SolidColorBrush(dark ? Color.FromRgb(0xB5, 0xCE, 0xA8) : Color.FromRgb(0x0F, 0x5F, 0x32)),
                Bracket = new SolidColorBrush(dark ? Color.FromRgb(0xDA, 0xAE, 0xF0) : Colors.Black),
                Separator = dark ? Brushes.White : Brushes.Black,
                Highlight = new SolidColorBrush(dark ? Color.FromRgb(0x38, 0x4F, 0x69) : Color.FromRgb(0xC3, 0xDC, 0xF9))
            };
            try {
                var component = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
                var service = component == null ? null :
                    component.GetService<IClassificationFormatMapService>();
                var registry = component == null ? null :
                    component.GetService<IClassificationTypeRegistryService>();
                var map = service == null || editor == null ? null :
                    service.GetClassificationFormatMap(editor);
                colors.Plain = map == null || map.DefaultTextProperties == null ||
                    map.DefaultTextProperties.ForegroundBrush == null ? colors.Plain :
                    map.DefaultTextProperties.ForegroundBrush;
                colors.Keyword = PreviewColor(map, registry,
                    JavaLanguageSupport.Editor.Intelligence.JavaKeywordClassificationNames.Keyword,
                    colors.Keyword);
                colors.Type = PreviewColor(map, registry,
                    JavaLanguageSupport.Editor.Intelligence.JavaSemanticClassificationNames.Type,
                    colors.Type);
                colors.Method = PreviewColor(map, registry,
                    JavaLanguageSupport.Editor.Intelligence.JavaSemanticClassificationNames.Method,
                    colors.Method);
                colors.Variable = PreviewColor(map, registry,
                    JavaLanguageSupport.Editor.Intelligence.JavaSemanticClassificationNames.Variable,
                    colors.Variable);
                colors.Namespace = PreviewColor(map, registry,
                    JavaLanguageSupport.Editor.Intelligence.JavaSemanticClassificationNames.Namespace,
                    colors.Namespace);
                colors.Bracket = PreviewColor(map, registry,
                    JavaLanguageSupport.Editor.Intelligence.JavaSemanticClassificationNames.Bracket,
                    colors.Bracket);
                colors.Separator = PreviewColor(map, registry,
                    JavaLanguageSupport.Editor.Intelligence.JavaSemanticClassificationNames.Separator,
                    colors.Separator);
                colors.Comment = PreviewColor(map, registry, "comment", colors.Comment);
                colors.Literal = PreviewColor(map, registry, "string", colors.Literal);
                colors.Number = PreviewColor(map, registry, "number", colors.Number);
            } catch (Exception) { /* Fall back to the documented Java palette. */ }
            return colors;
        }

        // Tokenizer exclusively for READ-ONLY preview; refactorings are still
        // produced by JDT LS and applied by the existing safe workspace engine.
        private static readonly Regex PreviewTokens = new Regex(
            @"//.*|""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|\b\d+(?:\.\d+)?\b|[A-Za-z_$][A-Za-z0-9_$]*|[()\[\]{}]|[.,;]",
            RegexOptions.Compiled);
        private static readonly HashSet<string> PreviewKeywords =
            new HashSet<string>(StringComparer.Ordinal) {
                "abstract", "assert", "boolean", "break", "byte", "case", "catch", "char",
                "class", "const", "continue", "default", "do", "double", "else", "enum",
                "extends", "final", "finally", "float", "for", "if", "implements", "import",
                "instanceof", "int", "interface", "long", "native", "new", "package",
                "private", "protected", "public", "record", "return", "short", "static",
                "strictfp", "super", "switch", "synchronized", "this", "throw", "throws",
                "transient", "try", "var", "void", "volatile", "while", "true", "false", "null"
            };

        private static void AddColoredRun(Paragraph paragraph, string source,
            int from, int to, Brush foreground, Brush highlight, int changedFrom, int changedTo)
        {
            int p = from;
            while (p < to) {
                bool changed = p >= changedFrom && p < changedTo;
                int end = to;
                if (p < changedFrom) end = Math.Min(end, changedFrom);
                else if (changed) end = Math.Min(end, changedTo);
                var run = new Run(source.Substring(p, end - p)) { Foreground = foreground };
                if (changed) {
                    run.Background = highlight;
                    run.FontWeight = FontWeights.SemiBold;
                }
                paragraph.Inlines.Add(run);
                p = end;
            }
        }

        // A single JDT LS TextEdit can replace a block containing two or more
        // occurrences. Build the proposed *whole document* (without modifying
        // any live buffer), then show context around ALL affected lines.
        private static int PreviewLineIndex(string source, int position)
        {
            int n = 0;
            for (int i = 0; i < Math.Min(Math.Max(0, position), source.Length); ++i)
                if (source[i] == '\n') n++;
            return n;
        }

        private static void AddJavaPreviewLine(FlowDocument document, string code,
            IList<Tuple<int, int>> changed, PreviewJavaColors colors)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0),
                LineHeight = 19, FontFamily = new FontFamily("Consolas"), FontSize = 12.5 };
            int cursor = 0;
            bool directive = code.TrimStart().StartsWith("package ", StringComparison.Ordinal) ||
                code.TrimStart().StartsWith("import ", StringComparison.Ordinal);
            foreach (Match token in PreviewTokens.Matches(code)) {
                if (token.Index > cursor)
                    AddPreviewRun(paragraph, code, cursor, token.Index,
                        colors.Plain, colors.Highlight, changed);
                string value = token.Value;
                Brush color;
                if (value.StartsWith("//", StringComparison.Ordinal)) color = colors.Comment;
                else if (value[0] == '"' || value[0] == '\'') color = colors.Literal;
                else if (char.IsDigit(value[0])) color = colors.Number;
                else if (PreviewKeywords.Contains(value)) color = colors.Keyword;
                else if ("()[]{}".IndexOf(value[0]) >= 0 && value.Length == 1)
                    color = colors.Bracket;
                else if (".,;".IndexOf(value[0]) >= 0 && value.Length == 1)
                    color = colors.Separator;
                else if (char.IsUpper(value[0])) color = colors.Type;
                else if (directive) color = colors.Namespace;
                else {
                    int next = token.Index + token.Length;
                    while (next < code.Length && char.IsWhiteSpace(code[next])) next++;
                    color = next < code.Length && code[next] == '(' ?
                        colors.Method : colors.Variable;
                }
                AddPreviewRun(paragraph, code, token.Index, token.Index + token.Length,
                    color, colors.Highlight, changed);
                cursor = token.Index + token.Length;
            }
            if (cursor < code.Length)
                AddPreviewRun(paragraph, code, cursor, code.Length,
                    colors.Plain, colors.Highlight, changed);
            if (code.Length == 0) paragraph.Inlines.Add(new Run(" "));
            document.Blocks.Add(paragraph);
        }

        private static void AddPreviewRun(Paragraph paragraph, string line,
            int from, int to, Brush foreground, Brush highlight,
            IList<Tuple<int, int>> intervals)
        {
            int at = from;
            foreach (var span in intervals.OrderBy(x => x.Item1)) {
                int begin = Math.Max(at, span.Item1);
                int end = Math.Min(to, span.Item2);
                if (begin >= end) continue;
                if (at < begin)
                    paragraph.Inlines.Add(new Run(line.Substring(at, begin - at)) {
                        Foreground = foreground });
                paragraph.Inlines.Add(new Run(line.Substring(begin, end - begin)) {
                    Foreground = foreground,
                    Background = highlight,
                    FontWeight = FontWeights.SemiBold });
                at = end;
            }
            if (at < to)
                paragraph.Inlines.Add(new Run(line.Substring(at, to - at)) {
                    Foreground = foreground });
        }

        private static void RenderPreviewCode(RichTextBox viewer,
            PreviewEntry selected, RenamePlan plan, PreviewJavaColors colors)
        {
            var document = new FlowDocument { PagePadding = new Thickness(10, 7, 10, 7),
                FontFamily = new FontFamily("Consolas"), FontSize = 12.5,
                Foreground = colors.Plain, Background = viewer.Background };
            var leaf = FirstPreviewLeaf(selected);
            if (leaf == null) {
                document.Blocks.Add(new Paragraph(new Run("Nessuna modifica da visualizzare.")));
            } else if (leaf.Move != null && selected.Move != null) {
                document.Blocks.Add(new Paragraph(new Run("Rinomina file .java:")));
                document.Blocks.Add(new Paragraph(new Run(leaf.Move.From)));
                document.Blocks.Add(new Paragraph(new Run("↓")));
                document.Blocks.Add(new Paragraph(new Run(leaf.Move.To)));
            } else if (leaf.Target != null) {
                // Find its file ancestor, so all checked edits in this document
                // are visible at the same time (not just the first LSP range).
                Target target = leaf.Target;
                PreviewEntry file = leaf;
                while (file.Parent != null &&
                    ReferenceEquals(file.Parent.Target, target)) file = file.Parent;
                var edits = file.Children.Where(x => x.Change != null &&
                    x.Check.IsChecked == true).Select(x => x.Change)
                    .OrderBy(x => x.Start).ToList();
                if (edits.Count == 0) {
                    document.Blocks.Add(new Paragraph(new Run(
                        "Nessuna modifica selezionata per questo documento.")));
                } else {
                    var modified = new StringBuilder(target.Before);
                    foreach (var edit in edits.OrderByDescending(x => x.Start)) {
                        modified.Remove(edit.Start, edit.End - edit.Start);
                        modified.Insert(edit.Start, edit.NewText ?? string.Empty);
                    }
                    string source = modified.ToString();
                    var mapped = new List<Tuple<Edit, int, int>>();
                    int shift = 0;
                    foreach (var edit in edits) {
                        int begin = edit.Start + shift;
                        int finish = begin + (edit.NewText ?? string.Empty).Length;
                        mapped.Add(Tuple.Create(edit, begin, finish));
                        shift += (edit.NewText ?? string.Empty).Length -
                            (edit.End - edit.Start);
                    }
                    Edit focus = edits.Contains(leaf.Change) ? leaf.Change : edits[0];
                    var focused = mapped.First(x => ReferenceEquals(x.Item1, focus));
                    int firstLine = PreviewLineIndex(source, focused.Item2);
                    int lastLine = PreviewLineIndex(source,
                        Math.Max(focused.Item2, focused.Item3 - 1));
                    // Include enough context AND all lines of this multiline edit.
                    var lines = source.Split('\n');
                    int fromLine = Math.Max(0, firstLine - 5);
                    int toLine = Math.Min(lines.Length - 1, lastLine + 6);
                    var lineStarts = new List<int> { 0 };
                    for (int i = 0; i < source.Length; ++i)
                        if (source[i] == '\n') lineStarts.Add(i + 1);
                    for (int i = fromLine; i <= toLine; i++) {
                        string code = lines[i].TrimEnd('\r');
                        int lineStart = lineStarts[i];
                        int lineEnd = lineStart + code.Length;
                        var changed = new List<Tuple<int, int>>();
                        foreach (var span in mapped) {
                            int begin = Math.Max(lineStart, span.Item2);
                            int end = Math.Min(lineEnd, span.Item3);
                            if (end <= begin) continue;
                            // For a multiline block edit, highlight the renamed
                            // symbol(s), not the entire unchanged surrounding line.
                            string pattern = @"(?<![A-Za-z0-9_$])" +
                                Regex.Escape(plan.NewName) + @"(?![A-Za-z0-9_$])";
                            foreach (Match match in Regex.Matches(code, pattern)) {
                                int global = lineStart + match.Index;
                                if (global >= begin && global + match.Length <= end)
                                    changed.Add(Tuple.Create(match.Index,
                                        match.Index + match.Length));
                            }
                            if (changed.Count == 0)
                                changed.Add(Tuple.Create(begin - lineStart,
                                    end - lineStart));
                        }
                        AddJavaPreviewLine(document, code, changed, colors);
                    }
                }
            }
            viewer.Document = document;
            viewer.ScrollToHome();
        }

        // WPF non offre un flag per nascondere SOLO "Riduci a icona" lasciando
        // ridimensionamento e chiusura. Eliminiamo WS_MINIMIZEBOX dall'HWND
        // della sola finestra di anteprima dopo SourceInitialized.
        private const int PreviewGwlStyle = -16;
        private const int PreviewWsMinimizeBox = 0x00020000;
        private const uint PreviewSwpNoSize = 0x0001;
        private const uint PreviewSwpNoMove = 0x0002;
        private const uint PreviewSwpNoZOrder = 0x0004;
        private const uint PreviewSwpFrameChanged = 0x0020;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int PreviewGetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int PreviewSetWindowLong(IntPtr hwnd, int index, int style);

        [DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PreviewSetWindowPos(IntPtr hwnd, IntPtr after,
            int x, int y, int width, int height, uint flags);

        private static void RemovePreviewMinimizeButton(WpfWindow window)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int style = PreviewGetWindowLong(hwnd, PreviewGwlStyle);
            if ((style & PreviewWsMinimizeBox) == 0) return;
            PreviewSetWindowLong(hwnd, PreviewGwlStyle, style & ~PreviewWsMinimizeBox);
            PreviewSetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                PreviewSwpNoMove | PreviewSwpNoSize | PreviewSwpNoZOrder |
                PreviewSwpFrameChanged);
        }

        // Pack C1 Hotfix 4: a JDT LS TextEdit can replace MULTIPLE lines.
        // A single edit may include both the class declaration and constructors;
        // OldText/NewText are then NOT equal to the simple type names. Validate
        // the resulting code, instead of comparing whole TextEdit strings.
        private static bool SelectedPreviewRenamesTopLevelType(RenamePlan plan,
            Target target, List<PreviewEntry> entries)
        {
            if (target == null || string.IsNullOrEmpty(target.Before)) return false;
            int declaration = FindTopLevelTypeName(target.Before, plan.OldName);
            if (declaration < 0) return false;

            var selectedEdits = entries.Where(x => ReferenceEquals(x.Target, target) &&
                x.Change != null && x.Check.IsChecked == true)
                .Select(x => x.Change).OrderByDescending(x => x.Start).ToList();

            // The edit must really cover the original verified declaration.
            // A change to a constructor, string or comment alone is not enough.
            bool coversDeclaration = selectedEdits.Any(x => x.Start <= declaration &&
                x.End >= declaration + plan.OldName.Length);
            if (!coversDeclaration) return false;

            var result = new StringBuilder(target.Before);
            foreach (var change in selectedEdits)
            {
                if (change.Start < 0 || change.End < change.Start ||
                    change.End > result.Length)
                    return false;
                result.Remove(change.Start, change.End - change.Start);
                result.Insert(change.Start, change.NewText ?? string.Empty);
            }
            string after = result.ToString();
            // Both checks matter: a separate type named NewName must not make
            // an unchanged declaration of OldName appear renamed.
            return FindTopLevelTypeName(after, plan.NewName) >= 0 &&
                FindTopLevelTypeName(after, plan.OldName) < 0;
        }

        // A physical Foo.java -> Bar.java move and the class declaration must
        // be selected together. The check also supports multiline TextEdits.
        private static bool ValidPreviewTypeMoves(RenamePlan plan,
            List<PreviewEntry> entries, out string problem)
        {
            problem = null;
            foreach (var move in plan.moves)
            {
                if (!PathCompare.Equals(Path.GetFileName(move.From), plan.OldName + ".java") ||
                    !PathCompare.Equals(Path.GetFileName(move.To), plan.NewName + ".java"))
                    continue;
                var target = plan.targets.FirstOrDefault(x =>
                    PathCompare.Equals(x.Path, move.From));
                bool selectedMove = entries.Any(x => ReferenceEquals(x.Move, move) &&
                    x.Check.IsChecked == true);
                bool selectedDeclaration = SelectedPreviewRenamesTopLevelType(
                    plan, target, entries);
                if (selectedMove != selectedDeclaration)
                {
                    problem = "La dichiarazione del tipo '" + plan.OldName +
                        "' e la rinomina di '" + Path.GetFileName(move.From) +
                        "' devono essere selezionate insieme. " +
                        "Seleziona entrambe le modifiche oppure deselezionale entrambe.";
                    return false;
                }
                // Do not accept a file move if the original top-level type
                // cannot be verified, even when an LSP edit was proposed.
                if (selectedMove && target == null)
                {
                    problem = "Dichiarazione Java non disponibile per " + move.From;
                    return false;
                }
            }
            return true;
        }

        // Pack C1 Preview/Theme Hotfix 2: the stock WPF Button template has
        // its own hover/focus triggers that can ignore Control.Foreground.
        // Set a local template that keeps white/dark text legible on its hover.
        // Base brushes still follow the Visual Studio dynamic theme resources.
        private static void ConfigurePreviewButton(Button button)
        {
            bool dark = false;
            try {
                var background = VSColorTheme.GetThemedColor(
                    EnvironmentColors.ToolWindowBackgroundColorKey);
                dark = background.R * 299 + background.G * 587 +
                    background.B * 114 < 128000;
            } catch (Exception) { }
            Brush hoverBackground = new SolidColorBrush(dark
                ? Color.FromRgb(0x37, 0x45, 0x55)
                : Color.FromRgb(0xD8, 0xE9, 0xF7));
            Brush pressedBackground = new SolidColorBrush(dark
                ? Color.FromRgb(0x28, 0x4A, 0x66)
                : Color.FromRgb(0xB9, 0xD8, 0xEF));
            Brush contrast = dark ? Brushes.White :
                (Brush)new SolidColorBrush(Color.FromRgb(0x17, 0x2D, 0x42));

            var template = new ControlTemplate(typeof(Button));
            var chrome = new FrameworkElementFactory(typeof(Border));
            chrome.Name = "PreviewButtonChrome";
            chrome.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
            chrome.SetValue(Border.PaddingProperty, new Thickness(12, 5, 12, 5));
            chrome.SetBinding(Border.BackgroundProperty, new Binding("Background") {
                RelativeSource = RelativeSource.TemplatedParent });
            chrome.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") {
                RelativeSource = RelativeSource.TemplatedParent });
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.Name = "PreviewButtonContent";
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty,
                HorizontalAlignment.Center);
            content.SetValue(FrameworkElement.VerticalAlignmentProperty,
                VerticalAlignment.Center);
            content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            content.SetBinding(TextElement.ForegroundProperty, new Binding("Foreground") {
                RelativeSource = RelativeSource.TemplatedParent });
            chrome.AppendChild(content);
            template.VisualTree = chrome;

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, hoverBackground,
                "PreviewButtonChrome"));
            hover.Setters.Add(new Setter(TextElement.ForegroundProperty, contrast,
                "PreviewButtonContent"));
            template.Triggers.Add(hover);
            var focus = new Trigger {
                Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
            focus.Setters.Add(new Setter(Border.BackgroundProperty, hoverBackground,
                "PreviewButtonChrome"));
            focus.Setters.Add(new Setter(TextElement.ForegroundProperty, contrast,
                "PreviewButtonContent"));
            template.Triggers.Add(focus);
            var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(Border.BackgroundProperty, pressedBackground,
                "PreviewButtonChrome"));
            pressed.Setters.Add(new Setter(TextElement.ForegroundProperty, contrast,
                "PreviewButtonContent"));
            template.Triggers.Add(pressed);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.48));
            template.Triggers.Add(disabled);
            button.Template = template;
        }

        internal static bool ShowPreview(WpfWindow owner, RenamePlan plan,
            ITextView sourceView)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var tree = new TreeView { BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch };
            tree.SetResourceReference(Control.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            tree.SetResourceReference(Control.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            ScrollViewer.SetHorizontalScrollBarVisibility(tree, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(tree, ScrollBarVisibility.Auto);
            var rootLabel = plan.OldName + "  →  " + plan.NewName;
            var declaring = plan.targets.FirstOrDefault(x =>
                Path.GetFileNameWithoutExtension(x.Path) == plan.OldName);
            if (declaring != null) {
                var package = Regex.Match(declaring.Before,
                    @"(?m)^\s*package\s+([\w.]+)\s*;");
                if (package.Success) rootLabel = package.Groups[1].Value + "." +
                    plan.OldName + "  →  " + plan.NewName;
            }
            var root = AddPreviewEntry(tree, null, rootLabel, "◆", null, null, null);
            var all = new List<PreviewEntry> { root };
            foreach (var target in plan.targets.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)) {
                var file = AddPreviewEntry(root.Item, root, Path.GetFileName(target.Path),
                    "▤", target, null, null, target.Path);
                all.Add(file);
                foreach (var edit in target.Edits.OrderBy(x => x.Start)) {
                    int line = target.Original.GetLineFromPosition(
                        Math.Min(edit.Start, Math.Max(0, target.Original.Length - 1))).LineNumber + 1;
                    var change = AddPreviewEntry(file.Item, file,
                        PreviewLineText(target, edit, plan.OldName, plan.NewName),
                        "↳", target, edit, null, edit.Category + " - " + target.Path);
                    all.Add(change);
                }
                foreach (var move in plan.moves.Where(x => PathCompare.Equals(x.From, target.Path))) {
                    var rename = AddPreviewEntry(file.Item, file,
                        "Rinomina file: " + Path.GetFileName(move.From) + " → " +
                        Path.GetFileName(move.To), "↳", target, null, move);
                    all.Add(rename);
                }
            }
            // Stato intermedio impostato solo dal codice: il click dell'utente
            // deve alternare inclusione/esclusione, non entrare in un terzo stato.

            var previewColors = GetPreviewColors(sourceView);
            var viewer = new RichTextBox { IsReadOnly = true,
                IsDocumentEnabled = false,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            viewer.SetResourceReference(Control.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            viewer.SetResourceReference(Control.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);

            var caption = new TextBlock {
                Text = "Rinomina '" + plan.OldName + "' in '" + plan.NewName + "':",
                Margin = new Thickness(0, 0, 0, 5)
            };
            caption.SetResourceReference(TextBlock.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            var previewLabel = new TextBlock { Text = "Anteprima modifiche codice:",
                Margin = new Thickness(0, 6, 0, 5) };
            previewLabel.SetResourceReference(TextBlock.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            var yes = new Button { Content = "Applica", MinWidth = 95, Margin = new Thickness(4),
                IsDefault = true };
            var no = new Button { Content = "Annulla", MinWidth = 95, Margin = new Thickness(4),
                IsCancel = true };
            // Pack C1 Undo Hotfix: colors are dynamic Visual Studio resources,
            // including the unfocused state of the Preview Changes buttons.
            foreach (Button button in new[] { yes, no }) {
                button.SetResourceReference(Control.BackgroundProperty,
                    EnvironmentColors.ToolWindowBackgroundBrushKey);
                button.SetResourceReference(Control.ForegroundProperty,
                    EnvironmentColors.ToolWindowTextBrushKey);
                button.SetResourceReference(Control.BorderBrushProperty,
                    EnvironmentColors.ToolWindowBorderBrushKey);
            }
            ConfigurePreviewButton(yes);
            ConfigurePreviewButton(no);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(yes);
            buttons.Children.Add(no);
            var upper = new Border { BorderThickness = new Thickness(1), Child = tree };
            upper.SetResourceReference(Border.BorderBrushProperty,
                EnvironmentColors.ToolWindowBorderBrushKey);
            var lower = new Border { BorderThickness = new Thickness(1), Child = viewer };
            lower.SetResourceReference(Border.BorderBrushProperty,
                EnvironmentColors.ToolWindowBorderBrushKey);
            // L'etichetta fa parte del pannello INFERIORE, non e' una riga
            // adiacente al GridSplitter: trascinando il separatore non puo'
            // essere collassata o scomparire.
            var lowerPanel = new Grid { MinHeight = 140 };
            lowerPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            lowerPanel.RowDefinitions.Add(new RowDefinition {
                Height = new GridLength(1, GridUnitType.Star), MinHeight = 95 });
            Grid.SetRow(previewLabel, 0);
            Grid.SetRow(lower, 1);
            lowerPanel.Children.Add(previewLabel);
            lowerPanel.Children.Add(lower);

            var splitter = new GridSplitter { Height = 6,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                ResizeDirection = GridResizeDirection.Rows,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                ShowsPreview = true };
            splitter.SetResourceReference(Control.BackgroundProperty,
                EnvironmentColors.ToolWindowBorderBrushKey);
            var content = new Grid { Margin = new Thickness(9) };
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition {
                Height = new GridLength(2, GridUnitType.Star), MinHeight = 95 });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
            content.RowDefinitions.Add(new RowDefinition {
                Height = new GridLength(1, GridUnitType.Star), MinHeight = 140 });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(caption, 0);
            Grid.SetRow(upper, 1);
            Grid.SetRow(splitter, 2);
            Grid.SetRow(lowerPanel, 3);
            Grid.SetRow(buttons, 4);
            content.Children.Add(caption);
            content.Children.Add(upper);
            content.Children.Add(splitter);
            content.Children.Add(lowerPanel);
            content.Children.Add(buttons);

            var window = new WpfWindow { Title = "Anteprima modifiche - Rinomina",
                Width = 850, Height = 610, MinWidth = 600, MinHeight = 405,
                ResizeMode = ResizeMode.CanResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = content, ShowInTaskbar = false };
            // The inline rename popup is intentionally frameless. Only the
            // separate preview dialog follows the common native caption policy.
            JavaLanguageSupport.Editor.Navigation.JavaNavigationWindowStyle.SetupWindow(window);
            window.SourceInitialized += (sender, args) => RemovePreviewMinimizeButton(window);
            if (owner != null && owner.IsVisible) window.Owner = owner;
            else {
                var shell = Package.GetGlobalService(typeof(SVsUIShell)) as IVsUIShell;
                IntPtr hwnd;
                if (shell != null && shell.GetDialogOwnerHwnd(out hwnd) >= 0 && hwnd != IntPtr.Zero)
                    new WindowInteropHelper(window).Owner = hwnd;
                else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            window.SetResourceReference(WpfWindow.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);

            bool changing = false;
            Action updateCount = () => {
                int selected = all.Count(x => x.Change != null && x.Check.IsChecked == true);
                int moves = all.Count(x => x.Move != null && x.Check.IsChecked == true);
                int files = all.Where(x => x.Target != null &&
                    (x.Change != null || x.Move != null) && x.Check.IsChecked == true)
                    .Select(x => x.Target.Path).Distinct(PathCompare).Count();
                // Pack C1: visible counts reflect only the *selected* leaves,
                // not the original all-inclusive JDT LS result.
                caption.Text = "Rinomina '" + plan.OldName + "' in '" + plan.NewName +
                    "': " + selected + " modifiche, " + moves +
                    " rinomine file, " + files + " file coinvolti.";
                yes.IsEnabled = selected + moves > 0;
                yes.ToolTip = selected + " modifiche al codice, " + moves + " rinomine file selezionate";
            };
            foreach (var entry in all) {
                var item = entry;
                item.Check.Checked += (s, e) => {
                    if (changing) return;
                    changing = true;
                    try { SetPreviewChildren(item, true); RefreshPreviewParents(item.Parent); }
                    finally { changing = false; }
                    item.Item.IsSelected = true;
                    updateCount();
                    var active = tree.SelectedItem as TreeViewItem;
                    if (active != null && active.Tag is PreviewEntry)
                        RenderPreviewCode(viewer, (PreviewEntry)active.Tag, plan, previewColors);
                };
                item.Check.Unchecked += (s, e) => {
                    if (changing) return;
                    changing = true;
                    try { SetPreviewChildren(item, false); RefreshPreviewParents(item.Parent); }
                    finally { changing = false; }
                    item.Item.IsSelected = true;
                    updateCount();
                    var active = tree.SelectedItem as TreeViewItem;
                    if (active != null && active.Tag is PreviewEntry)
                        RenderPreviewCode(viewer, (PreviewEntry)active.Tag, plan, previewColors);
                };
            }
            tree.SelectedItemChanged += (sender, args) => {
                var current = args.NewValue as TreeViewItem;
                if (current != null && current.Tag is PreviewEntry)
                    RenderPreviewCode(viewer, (PreviewEntry)current.Tag, plan, previewColors);
            };
            window.ContentRendered += (sender, args) => {
                viewer.ApplyTemplate();
                root.Item.IsSelected = true;
            };
            updateCount();
            yes.Click += (sender, args) => {
                if (!yes.IsEnabled) return;
                string problem;
                if (!ValidPreviewTypeMoves(plan, all, out problem)) {
                    System.Windows.MessageBox.Show(window, problem,
                        "Java: anteprima rinomina", MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
                window.DialogResult = true;
            };
            no.Click += (sender, args) => { window.DialogResult = false; };
            if (window.ShowDialog() != true) return false;

            // Cambia il piano SOLTANTO dopo Applica. Un Annulla non modifica nulla.
            var includedEdits = new HashSet<Edit>(all.Where(x => x.Change != null && x.Check.IsChecked == true)
                .Select(x => x.Change));
            var includedMoves = new HashSet<FileRename>(all.Where(x => x.Move != null && x.Check.IsChecked == true)
                .Select(x => x.Move));
            plan.moves.RemoveAll(x => !includedMoves.Contains(x));
            foreach (var target in plan.targets)
                target.Edits.RemoveAll(x => !includedEdits.Contains(x));
            plan.targets.RemoveAll(x => x.Edits.Count == 0 &&
                !plan.moves.Any(move => PathCompare.Equals(move.From, x.Path)));
            return plan.EditCount > 0 || plan.RenameCount > 0;
        }

        private static string Brief(string text)
        {
            if (text == null) return "";
            text = text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            return text.Length > 70 ? text.Substring(0, 70) + "…" : text;
        }

        private static void RefreshExplorerFiles(IEnumerable<FileRename> moves,
            Action<string> log)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Best-effort UI notification: a failure must not roll back an
            // already successful rename or introduce a new data-loss path.
            try
            {
                var changes = Package.GetGlobalService(typeof(SVsFileChangeEx))
                    as IVsFileChangeEx;
                if (changes == null)
                {
                    if (log != null) log("Explorer: servizio di notifica file non disponibile.");
                    return;
                }
                foreach (var move in moves)
                {
                    if (!File.Exists(move.To) || File.Exists(move.From))
                        continue;
                    changes.SyncFile(move.From);
                    changes.SyncFile(move.To);
                    if (log != null)
                        log("Explorer: aggiornamento richiesto per " +
                            Path.GetFileName(move.From) + " -> " + Path.GetFileName(move.To));
                }
            }
            catch (Exception ex)
            {
                if (log != null) log("Explorer: refresh non riuscito: " + ex.Message);
            }
        }

        // Pack C1 Undo Hotfix: a non-textual file move must participate in the
        // SAME VS undo transaction as all edits, including a rename started from
        // a reference in another open document.
        private sealed class RenameFileUndoPrimitive : ITextUndoPrimitive
        {
            private readonly List<FileRename> moves;
            private readonly List<Target> targets;
            internal RenameFileUndoPrimitive(IEnumerable<FileRename> fileMoves,
                IEnumerable<Target> documents)
            {
                moves = fileMoves.Select(x => new FileRename { From = x.From, To = x.To }).ToList();
                targets = documents.ToList();
            }
            public ITextUndoTransaction Parent { get; set; }
            public bool CanUndo { get { return true; } }
            public bool CanRedo { get { return true; } }
            public bool CanMerge(ITextUndoPrimitive older) { return false; }
            public ITextUndoPrimitive Merge(ITextUndoPrimitive older)
            {
                throw new NotSupportedException("Non unire la rinomina fisica a un'altra modifica.");
            }
            public void Do() { Move(true); } // Redo (initial Apply moves directly).
            public void Undo() { Move(false); }
            private void Move(bool redo)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var ordered = redo ? moves : moves.AsEnumerable().Reverse();
                var done = new List<FileRename>();
                try {
                    foreach (var move in ordered) {
                        string from = redo ? move.From : move.To;
                        string to = redo ? move.To : move.From;
                        var document = targets.FirstOrDefault(x => PathCompare.Equals(
                            x.Path, move.From));
                        if (document == null) throw new InvalidOperationException(
                            "Documento associato alla rinomina non trovato: " + from);
                        if (!File.Exists(from) || File.Exists(to))
                            throw new IOException("Undo/Redo rinomina: percorso inatteso: " + from + " -> " + to);
                        File.Move(from, to);
                        done.Add(new FileRename { From = from, To = to });
                        RenameOpenDocument(document, from, to);
                    }
                } catch {
                    foreach (var move in done.AsEnumerable().Reverse()) {
                        try {
                            var doc = targets.First(x => PathCompare.Equals(x.Path, move.From) ||
                                PathCompare.Equals(x.Path, move.To));
                            RenameOpenDocument(doc, move.To, move.From);
                            if (File.Exists(move.To) && !File.Exists(move.From))
                                File.Move(move.To, move.From);
                        } catch (Exception ex) {
                            System.Diagnostics.Trace.WriteLine("[Java Rename undo recovery] " + ex);
                        }
                    }
                    throw;
                }
                RefreshExplorerFiles(redo ? moves : moves.Select(x =>
                    new FileRename { From = x.To, To = x.From }), null);
                // VS linked undo reverts sibling buffers after individual primitives.
                // Persist only AFTER the entire undo/redo cycle reaches the dispatcher.
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                    new Action(() => {
                        foreach (var target in targets) {
                            try { if (target.Document.IsDirty) target.Document.Save(); }
                            catch (Exception ex) {
                                System.Diagnostics.Trace.WriteLine("[Java Rename undo save] " +
                                    target.Path + ": " + ex);
                            }
                        }
                    }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }

        private static void RenameOpenDocument(Target target, string from, string to)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var rdt = Package.GetGlobalService(typeof(SVsRunningDocumentTable))
                as IVsRunningDocumentTable;
            if (rdt == null) throw new InvalidOperationException(
                "RDT non disponibile: rinomina fisica non sicura.");
            Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(rdt.RenameDocument(
                from, to, Microsoft.VisualStudio.VSConstants.HIERARCHY_DONTCHANGE,
                Microsoft.VisualStudio.VSConstants.VSITEMID_NIL));
            if (!PathCompare.Equals(target.Document.FilePath, to))
                target.Document.Rename(to);
        }

        internal static int Apply(RenamePlan plan, Action<string> log)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (var target in plan.targets)
                if (target.Buffer.CurrentSnapshot.Version.VersionNumber != target.Original.Version.VersionNumber)
                    throw new InvalidOperationException("Il file e' stato modificato dopo l'anteprima: " + target.Path);
            foreach (var move in plan.moves)
                if (!File.Exists(move.From) || File.Exists(move.To))
                    throw new IOException("La destinazione del file e' cambiata: " + move.To);

            // Never silently save into an existing global undo transaction: it
            // belongs to another VS component and cannot be safely composed.
            var dte = Package.GetGlobalService(typeof(SDTE)) as DTE;
            if (dte == null || dte.UndoContext == null || dte.UndoContext.IsOpen)
                throw new InvalidOperationException(
                    "Undo globale Visual Studio non disponibile o gia' attivo; rinomina annullata.");
            var model = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
            var registry = model == null ? null : model.GetService<ITextUndoHistoryRegistry>();
            if (registry == null)
                throw new InvalidOperationException("ITextUndoHistoryRegistry mancante; nessuna modifica applicata.");

            // The source buffer is not necessarily the file whose physical name
            // changes. Its undo transaction is linked with all affected buffers.
            var anchor = plan.targets.FirstOrDefault(x => PathCompare.Equals(x.Path, plan.Origin))
                ?? plan.targets.FirstOrDefault(x => x.Edits.Count != 0)
                ?? plan.targets.FirstOrDefault();
            if (anchor == null) throw new InvalidOperationException("Nessun file nel piano di rinomina.");
            var history = registry.GetHistory(anchor.Buffer);
            if (history == null)
                throw new InvalidOperationException("Cronologia undo del buffer iniziale assente.");
            foreach (var target in plan.targets) {
                // Verify all participating documents have an undo manager before
                // applying any text: we refuse a partially undoable rename.
                if (registry.GetHistory(target.Buffer) == null)
                    throw new InvalidOperationException("Undo Java non disponibile: " + target.Path);
                string checkedPath = target.Document.FilePath;
                if (File.Exists(checkedPath) &&
                    (File.GetAttributes(checkedPath) & FileAttributes.ReadOnly) != 0)
                    throw new IOException("Sorgente di sola lettura: " + checkedPath);
            }

            var applied = new List<Target>();
            var moved = new List<FileRename>();
            ITextUndoTransaction fileUndo = null;
            bool linkedOpen = false;
            try {
                // EnvDTE.UndoContext uses the VS linked-undo service; a single
                // Ctrl+Z in any participating document must undo the whole rename.
                dte.UndoContext.Open("Java: Rinomina " + plan.OldName + " -> " + plan.NewName, true);
                linkedOpen = true;
                if (plan.moves.Count > 0)
                    fileUndo = history.CreateTransaction("Java: rinomina file .java");

                foreach (var target in plan.targets) {
                    if (target.Edits.Count == 0) continue;
                    applied.Add(target);
                    using (var transaction = target.Buffer.CreateEdit()) {
                        foreach (var r in target.Edits)
                            transaction.Replace(r.Start, r.End - r.Start, r.NewText);
                        if (transaction.Apply() == target.Original)
                            throw new InvalidOperationException("Impossibile modificare: " + target.Path);
                    }
                }
                foreach (var move in plan.moves) {
                    File.Move(move.From, move.To);
                    moved.Add(move);
                    var target = plan.targets.First(x => PathCompare.Equals(x.Path, move.From));
                    RenameOpenDocument(target, move.From, move.To);
                }
                if (fileUndo != null) {
                    fileUndo.AddUndo(new RenameFileUndoPrimitive(plan.moves, plan.targets));
                    fileUndo.Complete();
                    fileUndo.Dispose();
                    fileUndo = null;
                }
                dte.UndoContext.Close();
                linkedOpen = false;
                RefreshExplorerFiles(plan.moves, log);
            } catch (Exception ex) {
                var rollbackErrors = new List<string>();
                if (fileUndo != null) {
                    try { fileUndo.Cancel(); fileUndo.Dispose(); }
                    catch (Exception rollback) { rollbackErrors.Add(rollback.Message); }
                }
                // Abort linked undo before the disk rollback; it may revert
                // edits already recorded in other open document histories.
                if (linkedOpen) {
                    try { dte.UndoContext.SetAborted(); dte.UndoContext.Close(); }
                    catch (Exception rollback) { rollbackErrors.Add(rollback.Message); }
                }
                foreach (var move in moved.AsEnumerable().Reverse()) {
                    try {
                        var target = plan.targets.First(x => PathCompare.Equals(x.Path, move.From));
                        if (PathCompare.Equals(target.Document.FilePath, move.To))
                            RenameOpenDocument(target, move.To, move.From);
                        if (File.Exists(move.To) && !File.Exists(move.From)) File.Move(move.To, move.From);
                    } catch (Exception rollback) { rollbackErrors.Add(rollback.Message); }
                }
                foreach (var target in applied.AsEnumerable().Reverse()) {
                    try {
                        var current = target.Buffer.CurrentSnapshot;
                        if (!string.Equals(current.GetText(), target.Before, StringComparison.Ordinal))
                            target.Buffer.Replace(new Microsoft.VisualStudio.Text.Span(
                                0, current.Length), target.Before);
                    } catch (Exception rollback) { rollbackErrors.Add(rollback.Message); }
                }
                if (rollbackErrors.Count != 0)
                    throw new InvalidOperationException("Errore nella rinomina: " + ex.Message +
                        ". Ripristino incompleto: " + string.Join("; ", rollbackErrors), ex);
                throw;
            }

            // Pack C1 Undo Hotfix: persist *each changed buffer* after the linked
            // transaction is closed; otherwise JDT LS sees old on-disk class names
            // until the user manually invokes Save All.
            var saveErrors = new List<string>();
            foreach (var target in plan.targets) {
                try { if (target.Document.IsDirty) target.Document.Save(); }
                catch (Exception ex) { saveErrors.Add(target.Document.FilePath + ": " + ex.Message); }
            }
            if (saveErrors.Count > 0)
                throw new IOException("Rinomina applicata, ma alcuni file NON sono stati salvati: " +
                    string.Join(" | ", saveErrors) +
                    ". Il comando Annulla globale resta disponibile; verifica i file prima di continuare.");
            return plan.targets.Count;
        }

        private static string LocalJavaPath(string uri)
        {
            Uri address;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out address) || !address.IsFile)
                throw new NotSupportedException("WorkspaceEdit contiene un URI non locale: " + uri);
            string path = Path.GetFullPath(address.LocalPath);
            if (!string.Equals(Path.GetExtension(path), ".java", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("WorkspaceEdit contiene un file non Java: " + path);
            return path;
        }
        private static void Add(Dictionary<string, List<JToken>> result, string path, JArray edits)
        {
            if (edits == null) throw new NotSupportedException("WorkspaceEdit senza lista di modifiche.");
            List<JToken> list;
            if (!result.TryGetValue(path, out list)) result[path] = list = new List<JToken>();
            list.AddRange(edits);
        }
        private static int ToOffset(ITextSnapshot snapshot, JToken point)
        {
            if (point == null) throw new InvalidOperationException("Posizione LSP mancante.");
            int line = point.Value<int>("line"), col = point.Value<int>("character");
            if (line < 0 || line >= snapshot.LineCount)
                throw new InvalidOperationException("Riga LSP fuori intervallo.");
            var row = snapshot.GetLineFromLineNumber(line);
            if (col < 0 || col > row.Length)
                throw new InvalidOperationException("Colonna LSP fuori intervallo.");
            return row.Start.Position + col;
        }
        private static bool IsTypeDeclaration(string code, int offset)
        {
            int from = Math.Max(0, offset - 100);
            string prefix = code.Substring(from, offset - from);
            if (!Regex.IsMatch(prefix, @"\b(?:class|interface|enum|record)\s+$"))
                return false;
            // Solo tipi di primo livello: un tipo interno non deve rinominare
            // automaticamente il file esterno con nome accidentalmente uguale.
            int depth = 0, i = 0;
            while (i < offset) {
                if (i + 1 < offset && code[i] == '/' && code[i + 1] == '/') {
                    i += 2;
                    while (i < offset && code[i] != '\n' && code[i] != '\r') i++;
                } else if (i + 1 < offset && code[i] == '/' && code[i + 1] == '*') {
                    i += 2;
                    while (i + 1 < offset && !(code[i] == '*' && code[i + 1] == '/')) i++;
                    i += 2;
                } else if (code[i] == '"' || code[i] == '\'') {
                    char quote = code[i++];
                    bool triple = quote == '"' && i + 1 < offset &&
                        code[i] == '"' && code[i + 1] == '"';
                    if (triple) i += 2;
                    while (i < offset) {
                        if (code[i] == '\\') { i += 2; continue; }
                        if (!triple && code[i] == quote) { i++; break; }
                        if (triple && i + 2 < offset && code[i] == '"' &&
                            code[i + 1] == '"' && code[i + 2] == '"') { i += 3; break; }
                        i++;
                    }
                } else if (code[i] == '{') { depth++; i++; }
                else if (code[i] == '}') { depth--; i++; }
                else i++;
            }
            return depth == 0;
        }
        private static IEnumerable<string> EnumerateProjectJava(string root)
        {
            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                ".git", ".gradle", ".idea", ".settings", "target", "build", "out", "bin", "obj", ".venv"
            };
            var folders = new Stack<string>(); folders.Push(root);
            int files = 0;
            while (folders.Count > 0) {
                string folder = folders.Pop();
                foreach (var sub in Directory.EnumerateDirectories(folder))
                    if (!excluded.Contains(Path.GetFileName(sub))) folders.Push(sub);
                foreach (string path in Directory.EnumerateFiles(folder, "*.java")) {
                    if (++files > 2500)
                        throw new NotSupportedException("Progetto con piu' di 2500 file Java: rinomina testuale non eseguita. Disattiva commenti/stringhe.");
                    if (new FileInfo(path).Length > 2_000_000)
                        throw new NotSupportedException("Sorgente oltre 2MB: " + path);
                    yield return Path.GetFullPath(path);
                }
            }
        }
        // Scanner lessicale: non modifica il codice, i nomi parziali o i caratteri
        // di escape. Seleziona solo parole Java intere in commenti/stringhe.
        private static IEnumerable<Edit> FindTextualMatches(string text, string oldName,
            string newName, bool comments, bool strings)
        {
            int i = 0;
            while (i < text.Length) {
                int from = -1, to = -1; string kind = null;
                if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '/') {
                    from = i + 2; i = from;
                    while (i < text.Length && text[i] != '\n' && text[i] != '\r') i++;
                    to = i; kind = "Commento";
                } else if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*') {
                    from = i + 2; i = from;
                    while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/')) i++;
                    to = i + 1 < text.Length ? i : text.Length;
                    i = Math.Min(text.Length, to + 2); kind = "Commento";
                } else if (text[i] == '"') {
                    bool block = i + 2 < text.Length && text[i + 1] == '"' && text[i + 2] == '"';
                    from = i + (block ? 3 : 1);
                    i = from;
                    while (i < text.Length) {
                        if (text[i] == '\\' && i + 1 < text.Length) { i += 2; continue; }
                        if (text[i] == '"') {
                            if (!block) break;
                            if (i + 2 < text.Length && text[i + 1] == '"' && text[i + 2] == '"') break;
                        }
                        i++;
                    }
                    to = i;
                    i = Math.Min(text.Length, i + (block ? 3 : 1)); kind = "Stringa";
                } else if (text[i] == '\'') {
                    // Evita di riconoscere commenti all'interno di char literals.
                    i++;
                    while (i < text.Length) {
                        if (text[i] == '\\' && i + 1 < text.Length) { i += 2; continue; }
                        if (text[i++] == '\'') break;
                    }
                    continue;
                } else { i++; continue; }
                if ((kind == "Commento" && !comments) || (kind == "Stringa" && !strings))
                    continue;
                int p = from;
                while (p <= to - oldName.Length) {
                    int match = text.IndexOf(oldName, p, to - p, StringComparison.Ordinal);
                    if (match < 0) break;
                    int end = match + oldName.Length;
                    bool left = match == 0 || !JavaLetter(text[match - 1]);
                    bool right = end == text.Length || !JavaLetter(text[end]);
                    bool escaped = match > from && text[match - 1] == '\\';
                    if (left && right && !escaped)
                        yield return new Edit { Start = match, End = end, OldText = oldName,
                            NewText = newName, Category = kind };
                    p = end;
                }
            }
        }
        private static bool JavaLetter(char ch) => char.IsLetterOrDigit(ch) || ch == '_' || ch == '$';
    }
}
