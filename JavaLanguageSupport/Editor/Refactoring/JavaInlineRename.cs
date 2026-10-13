using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
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
    /// UI per rinomina inline: TextBox ancorato al simbolo dentro l'editor, senza DialogWindow.
    /// La rinomina reale e' calcolata da JDT LS e puo' modificare piu' documenti.
    /// Non implementa l'anteprima live di Roslyn mentre si digita.
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
            if (!Identifier(snapshot[caret])) { log("Posizionati su un simbolo Java per rinominarlo."); return; }
            int start = caret, end = caret + 1;
            while (start > 0 && Identifier(snapshot[start - 1])) start--;
            while (end < snapshot.Length && Identifier(snapshot[end])) end++;
            string oldName = snapshot.GetText(start, end - start);

            // Non lasciare una sessione precedente attiva.
            if (activeWindow != null && activeWindow.IsVisible)
                activeWindow.Close();

            var input = new TextBox
            {
                Text = oldName, MinWidth = 185, MaxWidth = 450,
                FontFamily = new FontFamily("Consolas"),
                Padding = new Thickness(5, 3, 5, 3),
                AcceptsReturn = false, AcceptsTab = false,
                Focusable = true,
                IsReadOnly = false,
                IsEnabled = true
            };
            input.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            input.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var caption = new TextBlock
            {
                Text = "Rinomina nel progetto  ·  Invio conferma  ·  Esc annulla",
                Margin = new Thickness(5, 5, 5, 4)
            };
            caption.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

            var confirm = new Button { Content = "Rinomina", MinWidth = 88, Margin = new Thickness(4) };
            var cancel = new Button { Content = "Annulla", MinWidth = 88, Margin = new Thickness(4) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(confirm);
            buttons.Children.Add(cancel);
            var content = new StackPanel { MinWidth = 360 };
            content.Children.Add(caption);
            content.Children.Add(input);
            content.Children.Add(buttons);
            var border = new Border { Padding = new Thickness(5), BorderThickness = new Thickness(1),
                Child = content };
            border.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            border.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);

            var line = view.TextViewLines.GetTextViewLineContainingBufferPosition(new SnapshotPoint(snapshot, caret));
            if (line == null) return;
            var bounds = line.GetCharacterBounds(new SnapshotPoint(snapshot, start));

            // WPF Popup non acquisisce sempre il focus di tastiera in devenv.exe.
            // Una Window modale senza chrome appare vicino al simbolo, ma disabilita
            // la finestra IDE finche' la rinomina non viene confermata/annullata.
            var window = new WpfWindow
            {
                Title = "Rinomina simbolo Java",
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = true,
                WindowStartupLocation = WindowStartupLocation.Manual,
                SizeToContent = SizeToContent.WidthAndHeight,
                Content = border,
                Focusable = true
            };
            window.SetResourceReference(WpfWindow.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);

            // Associa esplicitamente la Window all'HWND di Visual Studio; questo e'
            // essenziale per la modalita' e per non perdere il focus sul TextBox.
            WpfWindow owner = WpfWindow.GetWindow(view.VisualElement);
            if (owner != null && owner != window)
                window.Owner = owner;
            else
            {
                var shell = Package.GetGlobalService(typeof(SVsUIShell)) as IVsUIShell;
                IntPtr ownerHandle;
                if (shell != null && shell.GetDialogOwnerHwnd(out ownerHandle) >= 0 &&
                    ownerHandle != IntPtr.Zero)
                    new WindowInteropHelper(window).Owner = ownerHandle;
            }

            // PointToScreen restituisce pixel fisici; Window.Left/Top sono in DIP.
            var screen = view.VisualElement.PointToScreen(new System.Windows.Point(
                Math.Max(0, bounds.Left - view.ViewportLeft),
                Math.Max(0, bounds.Bottom - view.ViewportTop)));
            var source = PresentationSource.FromVisual(view.VisualElement);
            if (source != null && source.CompositionTarget != null)
                screen = source.CompositionTarget.TransformFromDevice.Transform(screen);
            window.Left = screen.X;
            window.Top = screen.Y;

            bool dispatched = false;
            bool renamePending = false;
            bool inputLocked = false;
            bool originalInputSetting = view.Options.GetOptionValue<bool>(
                DefaultTextViewOptions.ViewProhibitUserInputId);

            Action releaseEditor = () =>
            {
                if (!inputLocked) return;
                inputLocked = false;
                if (!view.IsClosed)
                    view.Options.SetOptionValue(DefaultTextViewOptions.ViewProhibitUserInputId,
                        originalInputSetting);
            };
            EventHandler viewClosed = null;
            viewClosed = (sender, args) => { if (window.IsVisible) window.Close(); };
            view.Closed += viewClosed;
            window.Closed += (sender, args) =>
            {
                view.Closed -= viewClosed;
                if (activeWindow == window) activeWindow = null;
                if (!renamePending) releaseEditor();
            };

            Action cancelRename = () =>
            {
                if (window.IsVisible) window.Close();
                // ShowDialog restituira' il controllo all'editor, lasciandolo intatto.
            };

            Action confirmRename = () =>
            {
                if (dispatched) return;
                string newName = input.Text.Trim();
                if (!ValidIdentifier(newName))
                {
                    Report(log, "Identificatore Java non valido: " + newName);
                    input.Focus();
                    return;
                }
                if (newName == oldName) { cancelRename(); return; }
                dispatched = true;
                renamePending = true;
                window.Close();
                var savedSnapshot = snapshot;
                var savedOffset = caret;
                ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
                {
                    try
                    {
                        var args = new
                        {
                            textDocument = new { uri },
                            position = JavaLspRpc.Position(savedSnapshot, savedOffset),
                            newName
                        };
                        JToken edit = await JavaLspRpc.RequestAsync("textDocument/rename", args,
                            CancellationToken.None).ConfigureAwait(false);
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        if (view.IsClosed || view.TextBuffer.CurrentSnapshot.Version.VersionNumber !=
                            savedSnapshot.Version.VersionNumber)
                            throw new InvalidOperationException(
                                "Il documento e' cambiato durante la rinomina; riprova.");
                        int changed = JavaProjectRenameEdits.Apply(edit, uri, manager, model, log);
                        Report(log, "Rinomina Java '" + oldName + "' -> '" + newName +
                            "': " + changed + " file aggiornati (non salvati).");
                    }
                    catch (Exception ex)
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        Report(log, "Rinomina Java annullata: " + ex.Message);
                    }
                    finally
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        releaseEditor();
                    }
                }).FileAndForget("JavaLanguageSupport/InlineRename");
            };

            // La finestra ha il focus OS. PreviewKeyDown intercetta anche Enter/Esc
            // prima delle scorciatoie dell'editor, mentre Backspace/Delete/lettere
            // vengono normalmente elaborate dalla TextBox.
            window.PreviewKeyDown += (sender, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    cancelRename();
                }
                else if (e.Key == Key.Enter || e.Key == Key.Return)
                {
                    e.Handled = true;
                    confirmRename();
                }
            };
            confirm.Click += (sender, e) => confirmRename();
            cancel.Click += (sender, e) => cancelRename();

            // Dopo la creazione dell'HWND reale, assegna il focus e seleziona
            // interamente il vecchio identificatore (Backspace lo cancellera').
            window.ContentRendered += (sender, e) =>
            {
                window.Activate();
                FocusManager.SetFocusedElement(window, input);
                input.Focus();
                Keyboard.Focus(input);
                input.SelectAll();
            };

            try
            {
                view.Options.SetOptionValue(DefaultTextViewOptions.ViewProhibitUserInputId, true);
                inputLocked = true;
                activeWindow = window;
                window.ShowDialog();
            }
            finally
            {
                // Qualunque uscita anomala non deve lasciare il sorgente bloccato.
                if (activeWindow == window) activeWindow = null;
                if (!renamePending) releaseEditor();
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
        private sealed class Edit { internal int Start, End; internal string NewText; }
        private sealed class Target
        {
            internal string Path;
            internal ITextBuffer Buffer;
            internal ITextSnapshot Original;
            internal List<Edit> Edits;
            internal string Before;
        }

        // Esegue un preflight completo PRIMA della prima scrittura. Nessun file e' salvato in automatico.
        internal static int Apply(JToken result, string originUri, IVsTextManager manager,
            IComponentModel model, Action<string> log)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var changes = Collect(result);
            if (changes.Count == 0) return 0;
            var adapters = model.GetService<IVsEditorAdaptersFactoryService>();
            var dte = Package.GetGlobalService(typeof(Microsoft.VisualStudio.Shell.Interop.SDTE)) as DTE;
            if (adapters == null || dte == null) throw new InvalidOperationException("Servizi Visual Studio non disponibili.");
            var origin = new Uri(originUri).LocalPath;
            var targets = new List<Target>();
            foreach (var group in changes)
            {
                var path = group.Key;
                if (!File.Exists(path) || Path.GetExtension(path).ToLowerInvariant() != ".java")
                    throw new NotSupportedException("Rinomina di file non Java o inesistenti non supportata: " + path);
                dte.ItemOperations.OpenFile(path, EnvDTE.Constants.vsViewKindTextView);
                IVsTextView current;
                if (manager.GetActiveView(1, null, out current) < 0 || current == null)
                    throw new InvalidOperationException("Impossibile aprire il buffer: " + path);
                var view = adapters.GetWpfTextView(current);
                if (view == null || !view.TextBuffer.ContentType.IsOfType("java"))
                    throw new InvalidOperationException("Buffer Java non disponibile: " + path);
                var snapshot = view.TextBuffer.CurrentSnapshot;
                var list = group.Value.Select(x => new Edit {
                    Start = ToOffset(snapshot, x["range"]?["start"]),
                    End = ToOffset(snapshot, x["range"]?["end"]),
                    NewText = (string)x["newText"] ?? ""
                }).OrderByDescending(x => x.Start).ToList();
                int previous = snapshot.Length + 1;
                foreach (var edit in list)
                {
                    if (edit.End < edit.Start || edit.End > previous)
                        throw new InvalidOperationException("Range LSP sovrapposti o invalidi: " + path);
                    previous = edit.Start;
                }
                targets.Add(new Target { Path = path, Buffer = view.TextBuffer,
                    Original = snapshot, Edits = list, Before = snapshot.GetText() });
            }
            // Le aperture dei file possono cambiare la selezione, ma non devono cambiare gli snapshot.
            foreach (var target in targets)
                if (target.Buffer.CurrentSnapshot.Version.VersionNumber != target.Original.Version.VersionNumber)
                    throw new InvalidOperationException("Un file e' cambiato prima dell'applicazione: " + target.Path);
            var applied = new List<Target>();
            try
            {
                foreach (var target in targets)
                {
                    using (var edit = target.Buffer.CreateEdit())
                    {
                        foreach (var r in target.Edits)
                            edit.Replace(r.Start, r.End - r.Start, r.NewText);
                        var appliedSnapshot = edit.Apply();
                        if (appliedSnapshot == target.Original)
                            throw new InvalidOperationException("Scrittura non eseguita: " + target.Path);
                    }
                    applied.Add(target);
                }
            }
            catch
            {
                foreach (var target in Enumerable.Reverse(applied))
                    target.Buffer.Replace(new Span(0, target.Buffer.CurrentSnapshot.Length), target.Before);
                throw;
            }
            dte.ItemOperations.OpenFile(origin, EnvDTE.Constants.vsViewKindTextView);
            return targets.Count;
        }

        private static Dictionary<string, List<JToken>> Collect(JToken response)
        {
            var result = new Dictionary<string, List<JToken>>(StringComparer.OrdinalIgnoreCase);
            if (!(response is JObject edit))
                throw new InvalidOperationException("JDT LS non ha restituito un WorkspaceEdit valido.");
            JArray documentChanges = edit["documentChanges"] as JArray;
            if (documentChanges != null)
            {
                foreach (JToken change in documentChanges)
                {
                    if (change["kind"] != null || change["textDocument"] == null)
                        throw new NotSupportedException("La rinomina richiede anche operazioni sui file (create/delete/rename). Non e' stata applicata.");
                    Add(result, (string)change["textDocument"]?["uri"], change["edits"] as JArray);
                }
            }
            else if (edit["changes"] is JObject changes)
                foreach (var item in changes.Properties()) Add(result, item.Name, item.Value as JArray);
            return result;
        }
        private static void Add(Dictionary<string, List<JToken>> result, string uri, JArray edits)
        {
            Uri address;
            if (edits == null || !Uri.TryCreate(uri, UriKind.Absolute, out address) || !address.IsFile)
                throw new NotSupportedException("WorkspaceEdit contiene un URI o un formato non supportato.");
            List<JToken> list;
            if (!result.TryGetValue(address.LocalPath, out list))
                result[address.LocalPath] = list = new List<JToken>();
            list.AddRange(edits);
        }
        private static int ToOffset(ITextSnapshot snapshot, JToken point)
        {
            if (point == null) throw new InvalidOperationException("Posizione LSP mancante.");
            int line = point.Value<int>("line"), col = point.Value<int>("character");
            if (line < 0 || line >= snapshot.LineCount) throw new InvalidOperationException("Riga LSP fuori intervallo.");
            var row = snapshot.GetLineFromLineNumber(line);
            if (col < 0 || col > row.Length) throw new InvalidOperationException("Colonna LSP fuori intervallo.");
            return row.Start.Position + col;
        }
    }
}
