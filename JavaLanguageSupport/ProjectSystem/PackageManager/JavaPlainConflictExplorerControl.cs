using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using JavaLanguageSupport.Editor.Navigation;
using Microsoft.VisualStudio.PlatformUI;

namespace JavaLanguageSupport.ProjectSystem.PackageManager
{
    // JPM-S2C-20261124: view and annotate conflict-resolution proposals only.
    // No Maven execution, no mutation of .javaproj/JARs or JDT LS state.
    // All ownership/versions come from S2A's verified archive metadata.
    internal sealed class JavaPlainConflictExplorerControl : UserControl
    {
        private sealed class Choice
        {
            internal JavaPlainVersionConflictGuard.Conflict Value;
            public string Display { get { return Value.Identity + "  (" +
                string.Join(" / ", Value.Versions) + ")"; } }
        }

        private readonly TreeView ownerTree = new TreeView();
        private readonly ListBox conflictList = new ListBox { DisplayMemberPath = "Display" };
        private readonly ComboBox strategyPicker = new ComboBox { MinWidth = 180, Width = 380 };
        private readonly ComboBox targetVersionPicker = new ComboBox { MinWidth = 160, Width = 215 };
        // JPM-S2C2-PREVIEW-TREE-THEME-20261124: TextBlock with bounded width
        // inside a vertical ScrollViewer; the preview must wrap, never crop.
        private readonly TextBlock preview = CreateLabel(
            "Seleziona un conflitto per visualizzare un piano di intervento.");
        // JPM-S2C3-BORDERED-PREVIEW-20261124: keep the independent frame.
        // JPM-S2C4-CONSTRAINED-PREVIEW-20261124: a 135px minimum on a 1* Grid
        // row forced the Border under the footer. Let the row limit the viewport;
        // long text stays inside the frame and is vertically scrollable.
        private readonly ScrollViewer previewScroller = new ScrollViewer {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            VerticalAlignment = VerticalAlignment.Stretch,
            MinHeight = 0,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0)
        };
        private readonly Border previewFrame = new Border {
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(105, 105, 105)),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, 4, 0, 4),
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        private readonly TextBlock summary = CreateLabel("Seleziona un progetto Plain.");
        private readonly TextBlock note = CreateLabel("Solo anteprima. Nessuna modifica automatica.");
        private readonly Button refreshButton = CreateButton("Aggiorna analisi");
        private readonly Button recordButton = CreateButton("Registra scelta in Output JPM");
        private readonly SemaphoreSlim scanGate = new SemaphoreSlim(1, 1);
        private JavaPlainVersionConflictGuard.Report report;
        private string projectFile;
        private long projectStamp;
        private long projectSize;
        private int generation;
        private string previewText;
        private bool validPlain;
        private bool rejectedCandidate;
        private string candidateOperation;

        private const string Align = "Valuta allineamento delle versioni (manuale)";
        private const string Keep = "Mantieni il classpath attuale";

        internal JavaPlainConflictExplorerControl()
        {
            var root = new Grid { Margin = new Thickness(9, 10, 9, 9) };
            root.SetResourceReference(Panel.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            // JPM-S2C4: both variable-height panes receive space and can scroll.
            // Keep the input selectors and footer in Auto rows; neither may overlap.
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 0 });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 0 });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var heading = CreateLabel("Conflitti Plain · dipendenze dirette e transitive");
            heading.FontSize = 15;
            heading.FontWeight = FontWeights.SemiBold;
            root.Children.Add(heading);
            var toolbar = new DockPanel { Margin = new Thickness(0, 8, 0, 8) };
            refreshButton.Margin = new Thickness(0, 0, 12, 0);
            DockPanel.SetDock(refreshButton, Dock.Left);
            toolbar.Children.Add(refreshButton);
            summary.VerticalAlignment = VerticalAlignment.Center;
            toolbar.Children.Add(summary);
            Grid.SetRow(toolbar, 1);
            root.Children.Add(toolbar);

            var lists = new Grid();
            lists.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            lists.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            var left = new DockPanel { Margin = new Thickness(0, 0, 7, 0) };
            var leftTitle = CreateLabel("Albero per dipendenza diretta (proprietario)");
            DockPanel.SetDock(leftTitle, Dock.Top);
            left.Children.Add(leftTitle);
            Theme(ownerTree);
            left.Children.Add(ownerTree);
            lists.Children.Add(left);
            var right = new DockPanel { Margin = new Thickness(7, 0, 0, 0) };
            var rightTitle = CreateLabel("Identita con versioni in conflitto");
            DockPanel.SetDock(rightTitle, Dock.Top);
            right.Children.Add(rightTitle);
            Theme(conflictList);
            right.Children.Add(conflictList);
            Grid.SetColumn(right, 1);
            lists.Children.Add(right);
            Grid.SetRow(lists, 2);
            root.Children.Add(lists);

            // JPM-S2C1-RESPONSIVE-COMBO-20261124: both selectors reuse the
            // established VS dark/light ComboBox style, including popup and
            // selected text. Put labels ABOVE controls to avoid clipping.
            var choices = new WrapPanel { Margin = new Thickness(0, 9, 0, 7) };
            var strategyGroup = new StackPanel { Margin = new Thickness(0, 0, 18, 8) };
            var actionTitle = CreateLabel("Strategia:");
            actionTitle.FontWeight = FontWeights.SemiBold;
            actionTitle.Margin = new Thickness(0, 0, 0, 5);
            strategyGroup.Children.Add(actionTitle);
            strategyPicker.Items.Add(Align);
            strategyPicker.Items.Add(Keep);
            strategyPicker.SelectedIndex = 0;
            JavaPackageManagerControl.ThemeProjectSelector(strategyPicker);
            // Strategies and versions are strings, not objects with DisplayName.
            strategyPicker.ItemTemplate = JavaPackageManagerControl.ListItemTemplate(null);
            strategyPicker.VerticalContentAlignment = VerticalAlignment.Center;
            strategyPicker.ToolTip = "Strategia consultiva: nessuna modifica automatica al progetto.";
            strategyGroup.Children.Add(strategyPicker);
            choices.Children.Add(strategyGroup);

            var versionGroup = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            var versionTitle = CreateLabel("Versione da valutare:");
            versionTitle.FontWeight = FontWeights.SemiBold;
            versionTitle.Margin = new Thickness(0, 0, 0, 5);
            versionGroup.Children.Add(versionTitle);
            JavaPackageManagerControl.ThemeProjectSelector(targetVersionPicker);
            targetVersionPicker.ItemTemplate = JavaPackageManagerControl.ListItemTemplate(null);
            targetVersionPicker.VerticalContentAlignment = VerticalAlignment.Center;
            targetVersionPicker.ToolTip = "Versione proposta, non applicata al classpath.";
            versionGroup.Children.Add(targetVersionPicker);
            choices.Children.Add(versionGroup);

            // Each control fits the tool window. At narrow widths the WrapPanel
            // moves the version group below the strategy group instead of
            // shrinking both selectors until their contents are unreadable.
            choices.SizeChanged += (sender, args) =>
            {
                double available = Math.Max(180.0, args.NewSize.Width - 18.0);
                double strategyWidth = Math.Min(380.0, available);
                double versionWidth = Math.Min(215.0, available);
                if (Math.Abs(strategyPicker.Width - strategyWidth) > 0.5)
                    strategyPicker.Width = strategyWidth;
                if (Math.Abs(targetVersionPicker.Width - versionWidth) > 0.5)
                    targetVersionPicker.Width = versionWidth;
            };
            Grid.SetRow(choices, 3);
            root.Children.Add(choices);

            // A ScrollViewer measures TextBlock against the available viewport
            // width, enabling multi-line wrapping and scrolling in short panes.
            preview.TextWrapping = TextWrapping.Wrap;
            preview.TextTrimming = TextTrimming.None;
            preview.SetResourceReference(TextBlock.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            previewScroller.Content = preview;
            previewScroller.Background = Brushes.Transparent;
            previewFrame.Child = previewScroller;
            previewFrame.SetResourceReference(Border.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            Grid.SetRow(previewFrame, 4);
            root.Children.Add(previewFrame);

            var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            DockPanel.SetDock(recordButton, Dock.Right);
            bottom.Children.Add(recordButton);
            note.VerticalAlignment = VerticalAlignment.Center;
            bottom.Children.Add(note);
            Grid.SetRow(bottom, 5);
            root.Children.Add(bottom);
            Content = root;

            refreshButton.Click += (s, e) => Reload();
            conflictList.SelectionChanged += (s, e) => ConflictChanged();
            strategyPicker.SelectionChanged += (s, e) => UpdatePreview();
            targetVersionPicker.SelectionChanged += (s, e) => UpdatePreview();
            recordButton.Click += (s, e) => RecordChoice();
            RefreshActions();
        }

        private static TextBlock CreateLabel(string value)
        {
            var label = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
            label.SetResourceReference(TextBlock.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            return label;
        }

        private static Button CreateButton(string caption)
        {
            var button = new Button { Content = caption, Padding = new Thickness(10, 5, 10, 5),
                MinHeight = 27, Margin = new Thickness(0, 0, 0, 0) };
            JavaNavigationWindowStyle.ThemeNavigationActionButton(button);
            return button;
        }

        private static void Theme(Control item)
        {
            item.SetResourceReference(Control.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            item.SetResourceReference(Control.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            item.SetResourceReference(Control.BorderBrushProperty,
                EnvironmentColors.ToolWindowBorderBrushKey);
        }

        // JPM-S2C2: use an explicit header template because the default
        // TreeViewItem template may use black OS foreground in the VS dark theme.
        // Default text inherits the Visual Studio theme brush; a selected header
        // uses blue + white to remain legible in BOTH light and dark themes.
        private static readonly DataTemplate ThemedTreeHeader = MakeTreeHeaderTemplate();

        private static DataTemplate MakeTreeHeaderTemplate()
        {
            var chrome = new FrameworkElementFactory(typeof(Border));
            chrome.Name = "JpmS2C2TreeHeaderChrome";
            chrome.SetValue(Border.PaddingProperty, new Thickness(3, 1, 3, 1));
            var caption = new FrameworkElementFactory(typeof(TextBlock));
            caption.Name = "JpmS2C2TreeHeaderText";
            caption.SetBinding(TextBlock.TextProperty,
                new System.Windows.Data.Binding());
            caption.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
            chrome.AppendChild(caption);
            var template = new DataTemplate { VisualTree = chrome };
            var selected = new DataTrigger {
                Binding = new System.Windows.Data.Binding("IsSelected") {
                    RelativeSource = new System.Windows.Data.RelativeSource(
                        System.Windows.Data.RelativeSourceMode.FindAncestor,
                        typeof(TreeViewItem), 1)
                },
                Value = true
            };
            selected.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush(Color.FromRgb(0, 122, 204)),
                "JpmS2C2TreeHeaderChrome"));
            selected.Setters.Add(new Setter(TextBlock.ForegroundProperty,
                Brushes.White, "JpmS2C2TreeHeaderText"));
            template.Triggers.Add(selected);
            return template;
        }

        private static TreeViewItem ThemedTreeItem(string caption)
        {
            var item = new TreeViewItem { Header = caption,
                HeaderTemplate = ThemedTreeHeader };
            item.SetResourceReference(Control.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            item.SetResourceReference(Control.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            return item;
        }

        internal void SetProject(string selected, bool isPlain)
        {
            Interlocked.Increment(ref generation);
            projectFile = selected;
            validPlain = isPlain && !string.IsNullOrWhiteSpace(selected);
            rejectedCandidate = false;
            candidateOperation = null;
            report = null;
            ownerTree.Items.Clear();
            conflictList.ItemsSource = null;
            targetVersionPicker.ItemsSource = null;
            previewText = null;
            preview.Text = "Seleziona un conflitto per visualizzare un piano di intervento.";
            summary.Text = validPlain ? "Conflitti Plain: analisi non ancora eseguita." :
                "Seleziona un progetto Plain.";
            note.Text = "Solo anteprima. Nessuna modifica automatica.";
            RefreshActions();
            if (validPlain) Reload();
        }

        // A candidate rejected by S2A is NOT in the installed classpath.
        // Present its structured snapshot separately until a new audit/commit.
        internal void ShowRejectedCandidate(string selected,
            JavaPlainVersionConflictGuard.Report candidate, string operation)
        {
            if (!validPlain || candidate == null ||
                !string.Equals(projectFile, selected, StringComparison.OrdinalIgnoreCase)) return;
            Interlocked.Increment(ref generation);
            rejectedCandidate = true;
            candidateOperation = operation;
            report = candidate;
            var info = new FileInfo(selected);
            projectStamp = info.Exists ? info.LastWriteTimeUtc.Ticks : 0;
            projectSize = info.Exists ? info.Length : 0;
            ownerTree.Items.Clear();
            conflictList.ItemsSource = null;
            targetVersionPicker.ItemsSource = null;
            previewText = null;
            Populate();
        }

        internal void Reload()
        {
            int stamp = Interlocked.Increment(ref generation);
            rejectedCandidate = false;
            candidateOperation = null;
            if (!validPlain || string.IsNullOrWhiteSpace(projectFile)) return;
            string selected = projectFile;
            report = null;
            conflictList.ItemsSource = null;
            ownerTree.Items.Clear();
            previewText = null;
            preview.Text = "Analisi in corso...";
            summary.Text = "Analisi conflitti e proprietari Plain in corso...";
            RefreshActions();
            _ = ReloadAsync(selected, stamp);
        }

        private async Task ReloadAsync(string selected, int stamp)
        {
            await scanGate.WaitAsync();
            try
            {
                if (stamp != Volatile.Read(ref generation) ||
                    !string.Equals(selected, projectFile, StringComparison.OrdinalIgnoreCase)) return;
                var fresh = await Task.Run(() => JavaPlainVersionConflictGuard.Audit(selected));
                if (stamp != Volatile.Read(ref generation) ||
                    !string.Equals(selected, projectFile, StringComparison.OrdinalIgnoreCase)) return;
                var info = new FileInfo(selected);
                projectStamp = info.Exists ? info.LastWriteTimeUtc.Ticks : 0;
                projectSize = info.Exists ? info.Length : 0;
                report = fresh;
                Populate();
            }
            catch (Exception ex)
            {
                if (stamp == Volatile.Read(ref generation) &&
                    string.Equals(selected, projectFile, StringComparison.OrdinalIgnoreCase))
                {
                    report = null;
                    summary.Text = "Analisi non riuscita. Dettagli in Output JPM.";
                    preview.Text = "Impossibile leggere le dipendenze del progetto.";
                    try { JavaJpmProjectLogger.Error(selected, "JPM S2C", ex); }
                    catch (Exception) { /* Logging opzionale. */ }
                }
            }
            finally
            {
                RefreshActions();
                scanGate.Release();
            }
        }

        private void Populate()
        {
            if (report == null) return;
            var conflicts = report.Conflicts ?? new List<JavaPlainVersionConflictGuard.Conflict>();
            var libraries = report.Libraries ?? new List<JavaPlainVersionConflictGuard.Library>();
            var conflicting = new HashSet<string>(conflicts.Select(c => c.Identity),
                StringComparer.OrdinalIgnoreCase);
            foreach (var owner in libraries.GroupBy(x => x.Owner,
                StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Key,
                    StringComparer.OrdinalIgnoreCase))
            {
                var root = ThemedTreeItem(owner.Key + " · " + owner.Count() + " JAR");
                var own = owner.Where(x => IsDirectJar(owner.Key, x)).OrderBy(x => x.Name,
                    StringComparer.OrdinalIgnoreCase).ToArray();
                var transitive = owner.Where(x => !IsDirectJar(owner.Key, x)).OrderBy(x => x.Name,
                    StringComparer.OrdinalIgnoreCase).ToArray();
                foreach (var library in own) root.Items.Add(JarNode(library, conflicting, "diretta"));
                if (transitive.Length != 0)
                {
                    var subgroup = ThemedTreeItem("Dipendenze transitive (" +
                        transitive.Length + ")");
                    foreach (var library in transitive)
                        subgroup.Items.Add(JarNode(library, conflicting, "transitiva"));
                    root.Items.Add(subgroup);
                }
                ownerTree.Items.Add(root);
            }
            conflictList.ItemsSource = conflicts.OrderBy(x => x.Identity,
                StringComparer.OrdinalIgnoreCase).Select(x => new Choice { Value = x }).ToList();
            summary.Text = (rejectedCandidate ? "⚠ Candidato rifiutato (" + candidateOperation + "): " : "") +
                "Conflitti: " + report.ConflictCount + "; JAR non verificabili: " +
                report.UnverifiedCount + "; riferimenti gestiti: " + libraries.Count + ".";
            note.Text = rejectedCandidate ?
                "Albero della risoluzione PROPOSTA, NON installata; classpath invariato." :
                "I JAR non verificabili o esterni non sono rappresentati nell'albero.";
            preview.Text = conflicts.Count == 0 ?
                "Nessun conflitto rilevato. Albero derivato dai JAR Plain gestiti; " +
                "non ricostruisce il grafo Maven interno dei pacchetti." :
                "Seleziona un conflitto a destra e la versione da valutare.";
            previewScroller.ScrollToTop();
            RefreshActions();
        }

        private static bool IsDirectJar(string owner, JavaPlainVersionConflictGuard.Library library)
        {
            string directOwner = owner ?? "";
            const string candidateTag = " (nuova risoluzione)";
            if (directOwner.EndsWith(candidateTag, StringComparison.Ordinal))
                directOwner = directOwner.Substring(0, directOwner.Length - candidateTag.Length);
            string[] parts = directOwner.Split(':');
            return parts.Length == 2 && string.Equals(library.Identity,
                parts[0] + "." + parts[1], StringComparison.OrdinalIgnoreCase);
        }

        private static TreeViewItem JarNode(JavaPlainVersionConflictGuard.Library value,
            HashSet<string> conflicting, string classification)
        {
            string prefix = conflicting.Contains(value.Identity) ? "⚠ " : "";
            var node = ThemedTreeItem(prefix + value.Identity + ":" + value.Version +
                " [" + value.Scope + "] · " + classification);
            node.ToolTip = value.Name;
            return node;
        }

        private void ConflictChanged()
        {
            var choice = conflictList.SelectedItem as Choice;
            targetVersionPicker.ItemsSource = choice == null ? null : choice.Value.Versions;
            targetVersionPicker.SelectedIndex = -1;
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            var choice = conflictList.SelectedItem as Choice;
            bool keep = string.Equals(strategyPicker.SelectedItem as string, Keep, StringComparison.Ordinal);
            targetVersionPicker.IsEnabled = choice != null && !keep;
            if (choice == null)
            {
                previewText = null;
                if (report != null && report.ConflictCount != 0)
                    preview.Text = "Seleziona un conflitto.";
                RefreshActions();
                return;
            }
            string target = targetVersionPicker.SelectedItem as string;
            var conflict = choice.Value;
            var output = new StringBuilder();
            output.AppendLine("ANTEPRIMA S2C — SOLO SIMULAZIONE, NESSUNA MODIFICA");
            output.AppendLine(rejectedCandidate ?
                "Fonte: candidato RIFIUTATO da S2A (non installato)." :
                "Fonte: classpath Plain installato al momento dell'analisi.");
            output.AppendLine("Identita: " + conflict.Identity);
            output.AppendLine("Versioni riscontrate: " + string.Join(", ", conflict.Versions));
            output.AppendLine("Radici dirette che includono la libreria:");
            foreach (var group in conflict.Libraries.GroupBy(x => x.Owner,
                StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Key,
                    StringComparer.OrdinalIgnoreCase))
                output.AppendLine("  " + group.Key + ": " + string.Join(", ", group.Select(x =>
                    x.Version + " [" + x.Scope + "]").Distinct(StringComparer.OrdinalIgnoreCase)));
            output.AppendLine();
            if (keep)
            {
                output.AppendLine("Scelta: non intervenire sul classpath.");
                output.AppendLine("Nessun file sara modificato.");
                previewText = output.ToString().TrimEnd();
            }
            else if (string.IsNullOrWhiteSpace(target))
            {
                output.AppendLine("Seleziona una versione di destinazione per valutare l'allineamento.");
                previewText = null;
            }
            else
            {
                output.AppendLine("Ipotesi: valutare l'allineamento a " + target + ".");
                foreach (var group in conflict.Libraries.Where(x =>
                    !string.Equals(x.Version, target, StringComparison.OrdinalIgnoreCase))
                    .GroupBy(x => x.Owner, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
                {
                    output.AppendLine("  DA RIVALUTARE " + group.Key + ": " +
                        string.Join(", ", group.Select(x => x.Version).Distinct(
                            StringComparer.OrdinalIgnoreCase)) + " -> " + target);
                }
                output.AppendLine();
                output.AppendLine("La compatibilita non e verificata; occorre scegliere versioni " +
                    "compatibili delle dipendenze dirette e rieseguire la risoluzione Maven.");
                output.AppendLine("NON cancellare JAR manualmente e NON modificare .classpath.");
                output.AppendLine("Nessun file sara modificato da questa anteprima.");
                previewText = output.ToString().TrimEnd();
            }
            preview.Text = output.ToString().TrimEnd();
            previewScroller.ScrollToTop();
            RefreshActions();
        }

        private void RefreshActions()
        {
            refreshButton.IsEnabled = validPlain;
            recordButton.IsEnabled = validPlain && report != null &&
                conflictList.SelectedItem is Choice && !string.IsNullOrEmpty(previewText);
        }

        private void RecordChoice()
        {
            if (!recordButton.IsEnabled || report == null ||
                string.IsNullOrWhiteSpace(previewText) || string.IsNullOrWhiteSpace(projectFile)) return;
            try
            {
                var info = new FileInfo(projectFile);
                if (!info.Exists || info.LastWriteTimeUtc.Ticks != projectStamp ||
                    info.Length != projectSize)
                {
                    note.Text = "Il progetto e cambiato; analisi da aggiornare. Scelta non registrata.";
                    Reload();
                    return;
                }
                JavaJpmProjectLogger.Info(projectFile, "JPM S2C - scelta non applicata", previewText);
                note.Text = "Scelta registrata in Output JPM, senza modificare il progetto.";
            }
            catch (Exception ex)
            {
                note.Text = "Registrazione non riuscita. Dettagli in Output JPM.";
                try { JavaJpmProjectLogger.Error(projectFile, "JPM S2C", ex); }
                catch (Exception) { /* Best-effort. */ }
            }
        }
    }
}
