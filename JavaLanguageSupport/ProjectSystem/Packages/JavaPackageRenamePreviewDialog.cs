using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using JavaLanguageSupport.Editor.Navigation;
using Microsoft.VisualStudio.PlatformUI;

namespace JavaLanguageSupport.ProjectSystem.Packages
{
internal static partial class JavaRenamePackageCommand
    {
        private sealed class JavaPackageRenamePreviewDialog : DialogWindow
        {
            private readonly TextBox details;

            internal JavaPackageRenamePreviewDialog(Plan plan)
            {
                Title = "Anteprima rinomina package Java";
                Width = 950; Height = 695; MinWidth = 690; MinHeight = 505;
                ResizeMode = ResizeMode.CanResize;
                ShowInTaskbar = false;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                var shell = new DockPanel { LastChildFill = true };
                Content = shell;

                var footer = new DockPanel { LastChildFill = true,
                    Margin = new Thickness(18, 9, 18, 14) };
                DockPanel.SetDock(footer, Dock.Bottom);
                shell.Children.Add(footer);
                var actions = new StackPanel { Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right };
                DockPanel.SetDock(actions, Dock.Right);
                footer.Children.Add(actions);
                var proceed = new Button { Content = "Applica rinomina", MinWidth = 153,
                    Height = 32, Margin = new Thickness(8, 0, 8, 0), IsDefault = true };
                var cancel = new Button { Content = "Annulla", MinWidth = 102,
                    Height = 32, IsCancel = true };
                JavaNavigationWindowStyle.ThemeNavigationActionButton(proceed);
                JavaNavigationWindowStyle.ThemeNavigationActionButton(cancel);
                actions.Children.Add(proceed); actions.Children.Add(cancel);
                proceed.Click += (s, e) => DialogResult = true;
                cancel.Click += (s, e) => DialogResult = false;

                var content = new DockPanel { LastChildFill = true,
                    Margin = new Thickness(20, 16, 20, 3) };
                shell.Children.Add(content);
                var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
                DockPanel.SetDock(heading, Dock.Top);
                content.Children.Add(heading);
                heading.Children.Add(Text("Verifica le modifiche prima di applicarle", 18, true));
                var explanation = Text("Visual Studio sposterà il package e " +
                    "aggiornerà i riferimenti individuati dall'analisi Java. " +
                    "Seleziona un file nell'elenco per esaminare i dettagli.", 12, false);
                explanation.Margin = new Thickness(0, 5, 0, 13);
                heading.Children.Add(explanation);
                heading.Children.Add(SectionLabel("RINOMINA PREVISTA"));
                var names = Text(plan.OldName + "  →  " + plan.NewName, 15, true);
                names.Margin = new Thickness(0, 5, 0, 6);
                heading.Children.Add(names);
                var location = Text("Da: " + plan.OldDirectory +
                    "\nA:  " + plan.NewDirectory, 11, false);
                heading.Children.Add(location);

                var summary = new WrapPanel { Margin = new Thickness(0, 13, 0, 1) };
                summary.Children.Add(Counter(plan.MovedJavaFiles.Count,
                    "FILE DA SPOSTARE"));
                summary.Children.Add(Counter(plan.Files.Count, "FILE DA MODIFICARE"));
                summary.Children.Add(Counter(plan.EditCount, "MODIFICHE AL CODICE"));
                heading.Children.Add(summary);

                var noteBorder = new Border { Padding = new Thickness(11, 8, 11, 8),
                    Margin = new Thickness(0, 10, 0, 0), BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3) };
                noteBorder.SetResourceReference(Border.BorderBrushProperty,
                    EnvironmentColors.ToolWindowBorderBrushKey);
                noteBorder.Child = Text("Le schede Java aperte e salvate rimarranno tali. " +
                    "Verrà creato un backup dei file prima della rinomina. " +
                    "\nNon è ancora disponibile l'annullamento globale con Ctrl+Z: " +
                    "per ripristinare l'intera operazione usa Git o il backup.", 11, false);
                heading.Children.Add(noteBorder);

                var columns = new Grid();
                columns.ColumnDefinitions.Add(new ColumnDefinition {
                    Width = new GridLength(310) });
                columns.ColumnDefinitions.Add(new ColumnDefinition {
                    Width = new GridLength(12) });
                columns.ColumnDefinitions.Add(new ColumnDefinition {
                    Width = new GridLength(1, GridUnitType.Star) });
                content.Children.Add(columns);

                var left = new DockPanel { LastChildFill = true };
                Grid.SetColumn(left, 0); columns.Children.Add(left);
                var leftTitle = SectionLabel("FILE COINVOLTI");
                leftTitle.Margin = new Thickness(0, 0, 0, 8);
                DockPanel.SetDock(leftTitle, Dock.Top); left.Children.Add(leftTitle);
                var files = new TreeView();
                Theme(files);
                System.Windows.Controls.ScrollViewer.SetVerticalScrollBarVisibility(
                    files, ScrollBarVisibility.Auto);
                left.Children.Add(files);

                var right = new DockPanel { LastChildFill = true };
                Grid.SetColumn(right, 2); columns.Children.Add(right);
                var detailTitle = SectionLabel("DETTAGLIO DEL FILE SELEZIONATO");
                detailTitle.Margin = new Thickness(0, 0, 0, 8);
                DockPanel.SetDock(detailTitle, Dock.Top); right.Children.Add(detailTitle);
                details = new TextBox { IsReadOnly = true, AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize = 12, Padding = new Thickness(11),
                    Text = "Seleziona un file a sinistra per vedere le modifiche previste." };
                Theme(details);
                right.Children.Add(details);

                string root = Path.GetDirectoryName(plan.ProjectFile);
                var moving = new TreeViewItem {
                    Header = "File da spostare (" + plan.MovedJavaFiles.Count + ")",
                    IsExpanded = true,
                    Tag = "La cartella verrà rinominata da:\n" + plan.OldDirectory +
                        "\n\na:\n" + plan.NewDirectory };
                Theme(moving);
                files.Items.Add(moving);
                foreach (string file in plan.MovedJavaFiles.OrderBy(x => x,
                    StringComparer.OrdinalIgnoreCase))
                {
                    string moved = Path.Combine(plan.NewDirectory, Path.GetFileName(file));
                    var item = new TreeViewItem {
                        Header = Path.GetFileName(file),
                        ToolTip = file,
                        Tag = "SPOSTAMENTO FILE\n\nDa: " + ShortPath(root, file) +
                            "\n\nA:  " + ShortPath(root, moved) +
                            "\n\nIl file manterrà il suo nome; cambierà " +
                            "la cartella che lo contiene." };
                    Theme(item);
                    moving.Items.Add(item);
                }
                var edited = new TreeViewItem {
                    Header = "Modifiche al codice (" + plan.Files.Count + " file)",
                    IsExpanded = true,
                    Tag = "Seleziona un file per controllare gli aggiornamenti " +
                          "alle dichiarazioni package, agli import e ai riferimenti." };
                Theme(edited);
                files.Items.Add(edited);
                foreach (var doc in plan.Files.OrderBy(x => x.Path,
                    StringComparer.OrdinalIgnoreCase))
                {
                    var description = new StringBuilder();
                    description.AppendLine("MODIFICHE AL CODICE");
                    description.AppendLine();
                    description.AppendLine(ShortPath(root, doc.Path));
                    description.AppendLine();
                    description.AppendLine(doc.EditCount + (doc.EditCount == 1 ?
                        " modifica prevista:" : " modifiche previste:"));
                    foreach (string change in doc.Preview)
                    {
                        description.AppendLine();
                        description.AppendLine(change.Trim());
                    }
                    TreeViewItem newItem = new TreeViewItem
                    {
                        Header = Path.GetFileName(doc.Path) + "  (" + doc.EditCount + ")",
                        ToolTip = doc.Path,
                        Tag = description.ToString()
                    };
                    Theme(newItem);
                    edited.Items.Add(newItem);
                }
                files.SelectedItemChanged += (s, e) => {
                    var selected = files.SelectedItem as TreeViewItem;
                    if (selected != null)
                        details.Text = selected.Tag as string ?? "";
                };
                // A changed file is more informative than the group header.
                var first = edited.Items.Count != 0 ? edited.Items[0] as TreeViewItem :
                    moving.Items.Count != 0 ? moving.Items[0] as TreeViewItem : null;
                if (first != null) first.IsSelected = true;
                JavaNavigationWindowStyle.SetupWindow(this);
            }

            private static string ShortPath(string root, string full)
            {
                string parent = Path.GetFullPath(root).TrimEnd('\\', '/') +
                    Path.DirectorySeparatorChar;
                return full.StartsWith(parent, StringComparison.OrdinalIgnoreCase) ?
                    full.Substring(parent.Length) : full;
            }

            private static Border Counter(int value, string description)
            {
                var border = new Border { Margin = new Thickness(0, 0, 8, 0),
                    Padding = new Thickness(12, 8, 12, 8), BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3) };
                border.SetResourceReference(Border.BorderBrushProperty,
                    EnvironmentColors.ToolWindowBorderBrushKey);
                var stack = new StackPanel(); border.Child = stack;
                stack.Children.Add(Text(value.ToString(), 19, true));
                stack.Children.Add(Text(description, 10, true));
                return border;
            }
        }
    }
}
