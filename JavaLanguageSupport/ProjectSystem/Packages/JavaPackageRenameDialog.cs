using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using JavaLanguageSupport.Editor.Navigation;
using Microsoft.VisualStudio.PlatformUI;

namespace JavaLanguageSupport.ProjectSystem.Packages
{
internal static partial class JavaRenamePackageCommand
    {
        // F2H1C-WPF-CLARITY: dialogs describe actions and impact, not release numbers.
        // The preview is read-only: the validated plan stays the single source of truth.
        private sealed class JavaPackageRenameDialog : DialogWindow
        {
            private readonly string oldName;
            private readonly string oldDirectory;
            private readonly TextBox field;
            private readonly TextBlock packagePreview;
            private readonly TextBlock pathPreview;
            private readonly TextBlock status;
            private readonly Button accept;
            internal string NewPackageName { get; private set; }

            internal JavaPackageRenameDialog(string directory, string originalName)
            {
                oldDirectory = directory;
                oldName = originalName;
                Title = "Rinomina package Java";
                Width = 700; Height = 510; MinWidth = 560; MinHeight = 435;
                ResizeMode = ResizeMode.CanResize;
                ShowInTaskbar = false;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                var shell = new DockPanel { LastChildFill = true };
                Content = shell;

                var footer = new StackPanel { Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(18, 8, 20, 14) };
                DockPanel.SetDock(footer, Dock.Bottom);
                shell.Children.Add(footer);
                accept = new Button { Content = "Visualizza anteprima", MinWidth = 160,
                    Height = 32, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
                var cancel = new Button { Content = "Annulla", MinWidth = 105,
                    Height = 32, IsCancel = true };
                JavaNavigationWindowStyle.ThemeNavigationActionButton(accept);
                JavaNavigationWindowStyle.ThemeNavigationActionButton(cancel);
                footer.Children.Add(accept);
                footer.Children.Add(cancel);
                accept.Click += (s, e) => { if (accept.IsEnabled) DialogResult = true; };
                cancel.Click += (s, e) => DialogResult = false;

                var scroll = new ScrollViewer {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                };
                shell.Children.Add(scroll);
                var body = new StackPanel { Margin = new Thickness(22, 18, 22, 15) };
                scroll.Content = body;

                body.Children.Add(Text("Rinomina un package Java", 18, true));
                var intro = Text("Scegli il nuovo nome. Prima di modificare i file, " +
                    "potrai esaminare tutte le operazioni proposte da Visual Studio.", 12, false);
                intro.Margin = new Thickness(0, 6, 0, 18);
                body.Children.Add(intro);

                body.Children.Add(SectionLabel("PACKAGE ATTUALE"));
                var current = Text(oldName, 14, true);
                current.Margin = new Thickness(0, 4, 0, 16);
                body.Children.Add(current);

                body.Children.Add(SectionLabel("NUOVO NOME (ULTIMO SEGMENTO)"));
                var hint = Text("Il package padre resta invariato. Modifica soltanto " +
                    "il nome finale, ad esempio da oldservice a newservice.", 11, false);
                hint.Margin = new Thickness(0, 3, 0, 7);
                body.Children.Add(hint);
                field = new TextBox { Height = 31, Padding = new Thickness(9, 4, 9, 4),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Text = oldName.Substring(oldName.LastIndexOf('.') + 1),
                    ToolTip = "Inserisci soltanto l'ultimo segmento del package Java" };
                Theme(field);
                body.Children.Add(field);

                var summary = new Border { Padding = new Thickness(12),
                    Margin = new Thickness(0, 17, 0, 0), BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4) };
                summary.SetResourceReference(Border.BorderBrushProperty,
                    EnvironmentColors.ToolWindowBorderBrushKey);
                var summaryBody = new StackPanel();
                summary.Child = summaryBody;
                summaryBody.Children.Add(SectionLabel("RISULTATO PREVISTO"));
                packagePreview = Text("", 14, true);
                packagePreview.Margin = new Thickness(0, 6, 0, 8);
                summaryBody.Children.Add(packagePreview);
                pathPreview = Text("", 11, false);
                summaryBody.Children.Add(pathPreview);
                body.Children.Add(summary);

                status = Text("", 11, false);
                status.Margin = new Thickness(0, 11, 0, 0);
                body.Children.Add(status);
                var note = Text("Le schede Java già aperte rimarranno aperte, purché " +
                    "le modifiche siano state salvate. " +
                    "\nLa rinomina verrà applicata " +
                    "solo dopo la conferma nell'anteprima.", 11, false);
                note.Margin = new Thickness(0, 8, 0, 0);
                body.Children.Add(note);

                field.TextChanged += (s, e) => Refresh();
                JavaNavigationWindowStyle.SetupWindow(this);
                Refresh();
                Loaded += (s, e) => { field.Focus(); field.SelectAll(); };
            }

            private void Refresh()
            {
                string tail = field.Text.Trim();
                string prefix = oldName.LastIndexOf('.') >= 0 ?
                    oldName.Substring(0, oldName.LastIndexOf('.') + 1) : "";
                string fullName = prefix + tail;
                string path = tail.Length == 0 ? "" :
                    Path.Combine(Path.GetDirectoryName(oldDirectory), tail);
                string invalid;
                bool valid = false;
                string explanation;
                if (tail.Length == 0)
                    explanation = "Inserisci il nuovo nome del package.";
                else if (tail.IndexOf('.') >= 0)
                    explanation = "Inserisci soltanto l'ultimo segmento, senza punti.";
                else if (tail.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    !JavaPackageResolver.TryValidatePackageName(fullName, out invalid) ||
                    ReservedWindowsName(tail))
                    explanation = "Il nome contiene caratteri non consentiti per un package Java.";
                else if (string.Equals(fullName, oldName, StringComparison.Ordinal))
                    explanation = "Il nuovo nome deve essere diverso da quello attuale.";
                else if (Directory.Exists(path) || File.Exists(path))
                    explanation = "Esiste già un file o una cartella con questo nome.";
                else
                {
                    valid = true;
                    explanation = "Pronto per l'anteprima: verranno verificate " +
                        "le dichiarazioni package e i riferimenti nel codice Java.";
                }

                packagePreview.Text = oldName + "\n↓\n" + (tail.Length == 0 ? "(nuovo nome)" : fullName);
                pathPreview.Text = "Cartella attuale: " + oldDirectory +
                    "\nNuova cartella: " + (path.Length == 0 ? "(da definire)" : path);
                status.Text = explanation;
                accept.IsEnabled = valid;
                NewPackageName = valid ? fullName : null;
            }
        }
    }
}
