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
using Microsoft.VisualStudio.Text.Editor;
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
                referenceCount.Text = "Calcolo delle modifiche con JDT LS…";
                ThreadHelper.JoinableTaskFactory.RunAsync(async delegate {
                    try {
                        var args2 = new { textDocument = new { uri },
                            position = JavaLspRpc.Position(snapshot, caret), newName = proposedName };
                        await renameGate.WaitAsync().ConfigureAwait(false);
                        JToken estimate;
                        try {
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                            // Ignora le stime superate senza intasare il Language Server.
                            if (!window.IsVisible || running || requestId != counterGeneration) return;
                            estimate = await JavaLspRpc.RequestAsync("textDocument/rename",
                                args2, CancellationToken.None).ConfigureAwait(false);
                        } finally {
                            renameGate.Release();
                        }
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        if (window.IsVisible && !running && requestId == counterGeneration)
                            referenceCount.Text = JavaProjectRenameEdits.DescribeRename(estimate);
                    } catch (Exception ex) {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        if (window.IsVisible && !running && requestId == counterGeneration)
                            referenceCount.Text = "Impossibile contare le modifiche: " + ex.Message;
                    }
                }).FileAndForget("JavaLanguageSupport/RenameEstimate");
            };
            input.TextChanged += (sender, args) => {
                if (running || !window.IsVisible) return;
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
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        if (view.IsClosed || view.TextBuffer.CurrentSnapshot.Version.VersionNumber !=
                            snapshot.Version.VersionNumber)
                            throw new InvalidOperationException("Il documento e' cambiato: ripeti la rinomina.");
                        var plan = JavaProjectRenameEdits.Prepare(edit, uri, oldName, newName,
                            includeComments.IsChecked == true, includeStrings.IsChecked == true,
                            manager, model, document.TextBuffer);
                        // Il conteggio definitivo proviene dal WorkspaceEdit che
                        // verra' applicato, non dalla risposta di references.
                        if (plan.EditCount == 0 && plan.RenameCount == 0)
                            throw new InvalidOperationException("JDT LS non ha individuato modifiche applicabili.");
                        referenceCount.Text = plan.EditCount + " modifiche in " + plan.FileCount +
                            " file; " + plan.RenameCount + " nomi di file .java da aggiornare.";
                        if ((preview || includeComments.IsChecked == true ||
                            includeStrings.IsChecked == true) &&
                            !JavaProjectRenameEdits.ShowPreview(WpfWindow.GetWindow(view.VisualElement), plan)) {
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
        }
        private static readonly StringComparer PathCompare = StringComparer.OrdinalIgnoreCase;

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
            IComponentModel model, ITextBuffer originBuffer)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(response is JObject edit))
                throw new InvalidOperationException("JDT LS non ha restituito un WorkspaceEdit valido.");
            Uri originAddress = new Uri(originUri);
            if (!originAddress.IsFile) throw new InvalidOperationException("Il file originale non e' locale.");
            string origin = Path.GetFullPath(originAddress.LocalPath);
            var plan = new RenamePlan { Origin = origin,
                Root = JavaWorkspace.Detect(origin).RootFolder };
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
                if (comments || strings)
                    foreach (var candidate in FindTextualMatches(target.Before, oldName, newName,
                        comments, strings))
                        if (!target.Edits.Any(x => candidate.Start < x.End && x.Start < candidate.End))
                            target.Edits.Add(candidate);
                target.Edits = target.Edits.OrderByDescending(x => x.Start).ToList();
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

        internal static bool ShowPreview(WpfWindow owner, RenamePlan plan)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var text = new StringBuilder();
            text.AppendLine("RINOMINA - ANTEPRIMA (nessuna modifica applicata)").AppendLine();
            foreach (var move in plan.moves)
                text.AppendLine("FILE: " + move.From + " -> " + move.To);
            foreach (var target in plan.targets) {
                text.AppendLine().AppendLine(target.Path + " (" + target.Edits.Count + " modifiche)");
                foreach (var edit in target.Edits.Take(60)) {
                    int line = target.Before.Take(edit.Start).Count(x => x == '\n') + 1;
                    text.AppendLine("  riga " + line + " [" + edit.Category + "]  " +
                        Brief(edit.OldText) + " -> " + Brief(edit.NewText));
                }
                if (target.Edits.Count > 60) text.AppendLine("  … altre modifiche non visualizzate");
            }
            var viewer = new TextBox { Text = text.ToString(), IsReadOnly = true,
                FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.NoWrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            viewer.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            viewer.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var yes = new Button { Content = "Applica modifiche", MinWidth = 115, Margin = new Thickness(5), IsDefault = true };
            var no = new Button { Content = "Torna alla rinomina", MinWidth = 120, Margin = new Thickness(5), IsCancel = true };
            var commands = new StackPanel { Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right };
            commands.Children.Add(yes); commands.Children.Add(no);
            var content = new DockPanel { Margin = new Thickness(9) };
            DockPanel.SetDock(commands, Dock.Bottom);
            content.Children.Add(commands); content.Children.Add(viewer);
            var window = new WpfWindow { Title = "Anteprima rinomina Java",
                Width = 820, Height = 520, MinWidth = 500, MinHeight = 250,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = content,
                ShowInTaskbar = false };
            // La finestra di rinomina e' gia' chiusa. Associa l'anteprima all'IDE
            // anche quando la finestra principale non e' una Window WPF.
            if (owner != null && owner.IsVisible) window.Owner = owner;
            else {
                var shell = Package.GetGlobalService(typeof(SVsUIShell)) as IVsUIShell;
                IntPtr hwnd;
                if (shell != null && shell.GetDialogOwnerHwnd(out hwnd) >= 0 && hwnd != IntPtr.Zero)
                    new WindowInteropHelper(window).Owner = hwnd;
                else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            window.SetResourceReference(WpfWindow.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            yes.Click += (sender, args) => { window.DialogResult = true; };
            no.Click += (sender, args) => { window.DialogResult = false; };
            return window.ShowDialog() == true;
        }

        private static string Brief(string text)
        {
            if (text == null) return "";
            text = text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            return text.Length > 70 ? text.Substring(0, 70) + "…" : text;
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
            var applied = new List<Target>();
            var moved = new List<FileRename>();
            try {
                foreach (var target in plan.targets) {
                    // renameFile senza TextEdit e' valido: non creare un edit vuoto.
                    if (target.Edits.Count == 0) continue;
                    using (var transaction = target.Buffer.CreateEdit()) {
                        foreach (var r in target.Edits)
                            transaction.Replace(r.Start, r.End - r.Start, r.NewText);
                        if (transaction.Apply() == target.Original)
                            throw new InvalidOperationException("Impossibile modificare: " + target.Path);
                    }
                    applied.Add(target);
                }
                foreach (var move in plan.moves) {
                    File.Move(move.From, move.To);
                    moved.Add(move);
                    var target = plan.targets.First(x => PathCompare.Equals(x.Path, move.From));
                    // Prima aggiorna il documento nell'RDT, poi il percorso del buffer.
                    var running = Package.GetGlobalService(typeof(SVsRunningDocumentTable))
                        as IVsRunningDocumentTable;
                    if (running != null)
                        Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(running.RenameDocument(
                            move.From, move.To, Microsoft.VisualStudio.VSConstants.HIERARCHY_DONTCHANGE,
                            Microsoft.VisualStudio.VSConstants.VSITEMID_NIL));
                    if (!PathCompare.Equals(target.Document.FilePath, move.To))
                        target.Document.Rename(move.To);
                }
                return plan.targets.Count;
            } catch (Exception ex) {
                var rollbackErrors = new List<string>();
                foreach (var move in moved.AsEnumerable().Reverse()) {
                    try {
                        var target = plan.targets.First(x => PathCompare.Equals(x.Path, move.From));
                        var running = Package.GetGlobalService(typeof(SVsRunningDocumentTable))
                            as IVsRunningDocumentTable;
                        if (running != null) running.RenameDocument(move.To, move.From,
                            Microsoft.VisualStudio.VSConstants.HIERARCHY_DONTCHANGE,
                            Microsoft.VisualStudio.VSConstants.VSITEMID_NIL);
                        if (PathCompare.Equals(target.Document.FilePath, move.To))
                            target.Document.Rename(move.From);
                        if (File.Exists(move.To)) File.Move(move.To, move.From);
                    } catch (Exception rollback) { rollbackErrors.Add(rollback.Message); }
                }
                foreach (var target in applied.AsEnumerable().Reverse()) {
                    try { target.Buffer.Replace(new Span(0, target.Buffer.CurrentSnapshot.Length), target.Before); }
                    catch (Exception rollback) { rollbackErrors.Add(rollback.Message); }
                }
                if (rollbackErrors.Count != 0)
                    throw new InvalidOperationException("Errore nella rinomina: " + ex.Message +
                        ". Ripristino incompleto: " + string.Join("; ", rollbackErrors), ex);
                throw;
            }
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
