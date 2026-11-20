using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml;
using Microsoft.VisualStudio.Shell;
using JavaLanguageSupport.Editor.Navigation;
using Microsoft.VisualStudio.PlatformUI;

namespace JavaLanguageSupport.ProjectSystem.PackageManager
{
    /// <summary>F4.F.2: dockable, read-only Java package browser.</summary>
    [System.Runtime.InteropServices.Guid("d4b00c0f-39f6-4e89-9acc-c19c2844f132")]
    public sealed class JavaPackageManagerToolWindow : ToolWindowPane
    {
        private readonly JavaPackageManagerControl view;
        public JavaPackageManagerToolWindow() : base(null)
        {
            Caption = "Pacchetti Java";
            view = new JavaPackageManagerControl();
            Content = view;
        }
        internal void SelectProject(string projectFile)
        {
            view.SelectProject(projectFile);
            Caption = "Pacchetti Java";
        }
    }

    internal sealed class JavaPackageManagerControl : UserControl
    {
        private readonly JavaMavenCentralSearchService service = new JavaMavenCentralSearchService();
        private readonly TextBlock projectHeader = Heading("Seleziona un progetto Java", 17);
        private readonly TextBlock projectInfo = CaptionText("Ricerca su Maven Central · sola lettura");
        private readonly TextBlock status = CaptionText("Pronto.");
        private readonly TextBox searchBox = new TextBox();
        private readonly Button searchButton = ActionButton("Cerca");
        private readonly Button cancelButton = ActionButton("Annulla");
        private readonly Button previousButton = ActionButton("◀ Precedente");
        private readonly Button nextButton = ActionButton("Successiva ▶");
        private readonly TextBlock pageInfo = CaptionText("");
        private readonly ListBox browseResults = new ListBox { DisplayMemberPath = "Coordinate" };
        private readonly TextBlock details = CaptionText("Seleziona una libreria per vedere i dettagli.");
        private readonly ListBox versionPicker = new ListBox(); // F4F21-THEMED-VERSIONS
        private readonly TextBlock versionInfo = CaptionText("");
        // F4F26-ALL-VERSIONS-PAGINATION: 30 results per request, never cap the list at page 1.
        private const int VersionPageSize = 30;
        private readonly Button previousVersionsButton = ActionButton("◀");
        private readonly Button nextVersionsButton = ActionButton("▶");
        private readonly TextBlock versionsPageInfo = CaptionText("");
        private readonly Button retryVersionsButton = ActionButton("Riprova versioni");
        private readonly TextBlock versionEmptyState = CaptionText("Seleziona una libreria."); // F4F24_EMPTY_VERSIONS
        private readonly ListBox installedResults = new ListBox();
        private readonly TextBlock installedNote = CaptionText("");
        private readonly TextBlock installedDetails = CaptionText("Seleziona una dipendenza.");
        private readonly ListBox updatesResults = new ListBox();
        private readonly TextBlock updatesNote = CaptionText("Confronto con le versioni pubblicate su Maven Central.");
        private readonly Button updateButton = ActionButton("Controlla versioni su Maven Central");
        private readonly TabControl tabs = new TabControl();
        private CancellationTokenSource searchCts;
        private CancellationTokenSource versionsCts;
        private CancellationTokenSource updatesCts;
        private JavaInstalledSnapshot installed;
        private string projectFile;
        private string query;
        private int currentPage = 1;
        private int searchGeneration;
        private int versionsGeneration;
        private int requestedVersionsPage = 1;
        private JavaCentralPage<JavaCentralVersion> lastVersionsPage;
        private int updatesGeneration;
        private JavaCentralPage<JavaCentralArtifact> lastPage;

        internal JavaPackageManagerControl()
        {
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 12;
            SetResourceReference(BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            SetResourceReference(ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            // F4F21-UI-THEME: explicit VS resources for the whole tool window.
            var root = new Grid { Margin = new Thickness(14, 12, 14, 8) };
            ThemeBackground(root);
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            projectHeader.Margin = new Thickness(0, 0, 0, 3);
            header.Children.Add(projectHeader);
            header.Children.Add(projectInfo);
            root.Children.Add(header);

            ThemeControl(tabs);
            tabs.Items.Add(ThemedTab("Sfoglia", MakeBrowse()));
            tabs.Items.Add(ThemedTab("Installati", MakeInstalled()));
            tabs.Items.Add(ThemedTab("Aggiornamenti", MakeUpdates()));
            ApplyNuGetLikeTabs(tabs); // F4F24_NUGET_TABS
            Grid.SetRow(tabs, 1);
            root.Children.Add(tabs);

            var statusPanel = new Border { Padding = new Thickness(2, 8, 2, 2), Child = status };
            Grid.SetRow(statusPanel, 2);
            root.Children.Add(statusPanel);
            Content = root;
            MinWidth = 470;
            MinHeight = 310;

            searchBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; _ = StartSearchAsync(1); }
            };
            searchButton.Click += (s, e) => { _ = StartSearchAsync(1); };
            cancelButton.Click += (s, e) => { CancelAll(); status.Text = "Richiesta annullata."; };
            previousButton.Click += (s, e) => { if (currentPage > 1) _ = StartSearchAsync(currentPage - 1); };
            nextButton.Click += (s, e) => { if (lastPage != null && lastPage.HasNextPage) _ = StartSearchAsync(currentPage + 1); };
            browseResults.SelectionChanged += (s, e) => { _ = LoadVersionsAsync(1, true); };
            previousVersionsButton.Click += (s, e) =>
            {
                if (lastVersionsPage != null && lastVersionsPage.Page > 1)
                    _ = LoadVersionsAsync(lastVersionsPage.Page - 1, false);
            };
            nextVersionsButton.Click += (s, e) =>
            {
                if (lastVersionsPage != null && lastVersionsPage.HasNextPage)
                    _ = LoadVersionsAsync(lastVersionsPage.Page + 1, false);
            };
            installedResults.SelectionChanged += (s, e) =>
            {
                var dep = installedResults.SelectedItem as JavaInstalledDependency;
                installedDetails.Text = dep == null ? "Seleziona una dipendenza." :
                    dep.Coordinate + "\nVersione dichiarata: " + dep.Version +
                    "\nOrigine: " + (installed == null ? "-" : installed.Backend);
            };
            updateButton.Click += (s, e) => { _ = LoadUpdatesAsync(); };
            retryVersionsButton.Click += (s, e) => { _ = LoadVersionsAsync(requestedVersionsPage, false); };
            tabs.SelectionChanged += (s, e) =>
            {
                if (e.Source == tabs && tabs.SelectedIndex == 1) RefreshInstalled();
            };
            Unloaded += (s, e) => CancelAll();
            SetSearchBusy(false);
            ResetVersionsPaging();
            versionPicker.IsHitTestVisible = false;
            ShowVersionPlaceholder("Seleziona una libreria per visualizzare le versioni.");
        }

        private FrameworkElement MakeBrowse()
        {
            var grid = new Grid { Margin = new Thickness(8, 12, 8, 8) };
            ThemeBackground(grid);
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var toolbar = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            searchBox.Margin = new Thickness(0, 0, 8, 0);
            searchBox.Padding = new Thickness(8, 4, 8, 4);
            searchBox.ToolTip = "Cerca nome libreria o coordinate groupId:artifactId";
            searchBox.Text = "";
            searchBox.SetResourceReference(Control.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            searchBox.SetResourceReference(Control.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            toolbar.Children.Add(searchBox);
            Grid.SetColumn(searchButton, 1);
            searchButton.Margin = new Thickness(0, 0, 8, 0);
            toolbar.Children.Add(searchButton);
            Grid.SetColumn(cancelButton, 2);
            toolbar.Children.Add(cancelButton);
            grid.Children.Add(toolbar);

            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 165 });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(7) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star), MinWidth = 190 });
            DecorateList(browseResults, "Coordinate");
            content.Children.Add(browseResults);
            var splitter = new GridSplitter
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Width = 5, Background = Brushes.Transparent
            };
            Grid.SetColumn(splitter, 1);
            content.Children.Add(splitter);
            var right = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
            right.Children.Add(Heading("Dettagli pacchetto", 15));
            details.TextWrapping = TextWrapping.Wrap;
            details.Margin = new Thickness(0, 10, 0, 13);
            right.Children.Add(details);
            right.Children.Add(Heading("Versioni disponibili", 13));
            // F4.F.2.4: a disabled WPF ListBox can paint a white rectangle
            // under VS Dark. Keep it enabled and overlay a themed empty state.
            var versionHost = new Grid { Margin = new Thickness(0, 6, 0, 8),
                Height = 158, MinHeight = 84 };
            versionPicker.Margin = new Thickness(0);
            versionPicker.IsEnabled = true;
            DecorateList(versionPicker, "Version");
            versionHost.Children.Add(versionPicker);
            versionEmptyState.Margin = new Thickness(15, 12, 15, 12);
            versionEmptyState.VerticalAlignment = VerticalAlignment.Center;
            versionEmptyState.HorizontalAlignment = HorizontalAlignment.Center;
            versionEmptyState.TextAlignment = TextAlignment.Center;
            versionEmptyState.IsHitTestVisible = false;
            versionHost.Children.Add(versionEmptyState);
            right.Children.Add(versionHost);
            // Separate pager: package search pagination is independent of version pagination.
            var versionsPager = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 2, 0, 8)
            };
            previousVersionsButton.Margin = new Thickness(0, 0, 8, 0);
            versionsPageInfo.Margin = new Thickness(0, 0, 8, 0);
            versionsPageInfo.VerticalAlignment = VerticalAlignment.Center;
            previousVersionsButton.ToolTip = "Pagina precedente delle versioni";
            nextVersionsButton.ToolTip = "Pagina successiva delle versioni";
            versionsPager.Children.Add(previousVersionsButton);
            versionsPager.Children.Add(versionsPageInfo);
            versionsPager.Children.Add(nextVersionsButton);
            right.Children.Add(versionsPager);
            versionInfo.TextWrapping = TextWrapping.Wrap;
            right.Children.Add(versionInfo);
            retryVersionsButton.Margin = new Thickness(0, 6, 0, 0);
            retryVersionsButton.HorizontalAlignment = HorizontalAlignment.Left;
            retryVersionsButton.Visibility = Visibility.Collapsed;
            right.Children.Add(retryVersionsButton);
            var readOnly = CaptionText("Installazione, aggiornamento e rimozione arriveranno in F4.F.3–F4.F.5.");
            readOnly.Margin = new Thickness(0, 18, 0, 0);
            readOnly.TextWrapping = TextWrapping.Wrap;
            right.Children.Add(readOnly);
            var rightScroll = new ScrollViewer
            {
                Content = right,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetColumn(rightScroll, 2);
            content.Children.Add(rightScroll);
            Grid.SetRow(content, 1);
            grid.Children.Add(content);

            var pager = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 10, 0, 0)
            };
            previousButton.Margin = new Thickness(0, 0, 10, 0);
            pageInfo.VerticalAlignment = VerticalAlignment.Center;
            pageInfo.Margin = new Thickness(0, 0, 10, 0);
            pager.Children.Add(previousButton);
            pager.Children.Add(pageInfo);
            pager.Children.Add(nextButton);
            Grid.SetRow(pager, 2);
            grid.Children.Add(pager);
            return grid;
        }

        private FrameworkElement MakeInstalled()
        {
            var grid = new Grid { Margin = new Thickness(8, 12, 8, 8) };
            ThemeBackground(grid);
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(Heading("Dipendenze dichiarate nel progetto", 15));
            DecorateList(installedResults, null);
            installedResults.Margin = new Thickness(0, 10, 0, 8);
            Grid.SetRow(installedResults, 1);
            grid.Children.Add(installedResults);
            installedDetails.TextWrapping = TextWrapping.Wrap;
            installedDetails.Margin = new Thickness(0, 4, 0, 9);
            Grid.SetRow(installedDetails, 2);
            grid.Children.Add(installedDetails);
            installedNote.TextWrapping = TextWrapping.Wrap;
            Grid.SetRow(installedNote, 3);
            grid.Children.Add(installedNote);
            return grid;
        }

        private FrameworkElement MakeUpdates()
        {
            var grid = new Grid { Margin = new Thickness(8, 12, 8, 8) };
            ThemeBackground(grid);
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(Heading("Verifica versioni disponibili", 15));
            updateButton.Margin = new Thickness(0, 10, 0, 10);
            updateButton.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetRow(updateButton, 1);
            grid.Children.Add(updateButton);
            DecorateList(updatesResults, null);
            Grid.SetRow(updatesResults, 2);
            grid.Children.Add(updatesResults);
            updatesNote.Margin = new Thickness(0, 9, 0, 0);
            updatesNote.TextWrapping = TextWrapping.Wrap;
            Grid.SetRow(updatesNote, 3);
            grid.Children.Add(updatesNote);
            return grid;
        }

        internal void SelectProject(string newProjectFile)
        {
            if (string.IsNullOrWhiteSpace(newProjectFile)) return;
            if (string.Equals(projectFile, newProjectFile, StringComparison.OrdinalIgnoreCase))
            {
                RefreshInstalled();
                return;
            }
            CancelAll();
            projectFile = newProjectFile;
            query = null;
            currentPage = 1;
            lastPage = null;
            browseResults.ItemsSource = null;
            details.Text = "Seleziona una libreria per vedere i dettagli.";
            versionPicker.ItemsSource = null;
            versionPicker.IsHitTestVisible = false;
            ResetVersionsPaging();
            ShowVersionPlaceholder("Seleziona una libreria per visualizzare le versioni.");
            pageInfo.Text = "";
            updatesResults.ItemsSource = null;
            projectHeader.Text = System.IO.Path.GetFileNameWithoutExtension(projectFile);
            RefreshInstalled();
            tabs.SelectedIndex = 0;
            status.Text = "Pronto. Inserisci una ricerca su Maven Central.";
        }

        private void RefreshInstalled()
        {
            if (string.IsNullOrWhiteSpace(projectFile)) return;
            try
            {
                installed = JavaPackageManagerInstalledReader.Read(projectFile);
                installedResults.ItemsSource = installed.Items.ToList();
                installedNote.Text = installed.Note;
                projectInfo.Text = installed.Backend + " · " + installed.Items.Count +
                    " dipendenze dirette · Maven Central (sola lettura)";
                updatesNote.Text = "Il confronto indica versioni diverse, non garantisce un " +
                    "aggiornamento semantico. Non vengono modificati file di progetto.";
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is XmlException ||
                ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                installed = null;
                installedResults.ItemsSource = null;
                projectInfo.Text = "Impossibile leggere le dipendenze locali.";
                installedNote.Text = ex.Message;
            }
        }

        private async Task StartSearchAsync(int page)
        {
            if (string.IsNullOrWhiteSpace(projectFile)) return;
            string search = searchBox.Text.Trim();
            if (search.Length == 0) { status.Text = "Inserire un termine di ricerca."; return; }
            if (!string.Equals(query, search, StringComparison.Ordinal)) page = 1;
            query = search;
            int generation = ++searchGeneration;
            ReplaceToken(ref searchCts);
            ReplaceToken(ref versionsCts);
            ++versionsGeneration;
            ResetVersionsPaging();
            versionPicker.ItemsSource = null;
            versionPicker.IsHitTestVisible = false;
            retryVersionsButton.Visibility = Visibility.Collapsed;
            ShowVersionPlaceholder("Seleziona una libreria per visualizzare le versioni.");
            details.Text = "Seleziona una libreria per vedere i dettagli.";
            versionInfo.Text = "";
            var token = searchCts.Token;
            SetSearchBusy(true);
            status.Text = "Ricerca in corso su Maven Central...";
            try
            {
                var found = await service.SearchAsync(search, page, 20, token);
                if (generation != searchGeneration || token.IsCancellationRequested) return;
                lastPage = found;
                currentPage = page;
                browseResults.ItemsSource = found.Items.ToList();
                // First result becomes visible and details/versions load immediately.
                if (found.Items.Count > 0) browseResults.SelectedIndex = 0;
                pageInfo.Text = "Pagina " + page + " · " + found.TotalCount + " risultati";
                previousButton.IsEnabled = currentPage > 1;
                nextButton.IsEnabled = found.HasNextPage;
                status.Text = found.Items.Count == 0 ? "Nessuna libreria trovata." :
                    "Ricerca completata: " + found.TotalCount + " risultati.";
            }
            catch (OperationCanceledException) { if (generation == searchGeneration) status.Text = "Ricerca annullata."; }
            catch (Exception ex) when (ex is JavaMavenCentralSearchException ||
                ex is ArgumentException || ex is ArgumentOutOfRangeException)
            { if (generation == searchGeneration) status.Text = "Ricerca: " + ex.Message; }
            finally { if (generation == searchGeneration) SetSearchBusy(false); }
        }

        private void ResetVersionsPaging()
        {
            requestedVersionsPage = 1;
            lastVersionsPage = null;
            previousVersionsButton.IsEnabled = false;
            nextVersionsButton.IsEnabled = false;
            versionsPageInfo.Text = "";
        }

        private async Task LoadVersionsAsync(int page, bool debounceSelection)
        {
            var artifact = browseResults.SelectedItem as JavaCentralArtifact;
            int generation = ++versionsGeneration;
            ReplaceToken(ref versionsCts);
            // Keep the requested page on failures, so Retry does not silently jump to page 1.
            requestedVersionsPage = page;
            lastVersionsPage = null;
            previousVersionsButton.IsEnabled = false;
            nextVersionsButton.IsEnabled = false;
            versionsPageInfo.Text = artifact == null ? "" : "Pagina " + page + " · caricamento...";
            retryVersionsButton.Visibility = Visibility.Collapsed;
            versionPicker.ItemsSource = null;
            versionPicker.IsHitTestVisible = false;
            if (artifact == null)
            {
                ResetVersionsPaging();
                ShowVersionPlaceholder("Seleziona una libreria per visualizzare le versioni.");
                versionInfo.Text = "";
                return;
            }
            ShowVersionPlaceholder("Caricamento versioni...");
            details.Text = artifact.Coordinate + "\nUltima versione: " + artifact.LatestVersion +
                "\nTipo: " + artifact.Packaging + "\nVersioni pubblicate: " + artifact.VersionCount +
                "\nPubblicazione: " + (artifact.PublishedUtc.HasValue
                    ? artifact.PublishedUtc.Value.ToString("yyyy-MM-dd") : "n/d");
            versionInfo.Text = "Caricamento pagina " + page + "...";
            var token = versionsCts.Token;
            try
            {
                // Only a newly selected artifact is debounced; page buttons should respond immediately.
                if (debounceSelection)
                    await Task.Delay(300, token);
                var versions = await service.GetVersionsAsync(artifact.GroupId,
                    artifact.ArtifactId, page, VersionPageSize, token);
                if (generation != versionsGeneration || token.IsCancellationRequested) return;
                lastVersionsPage = versions;
                versionPicker.ItemsSource = versions.Items.ToList();
                versionPicker.IsHitTestVisible = versions.Items.Count > 0;
                ShowVersionPlaceholder(versions.Items.Count == 0
                    ? "Nessuna versione disponibile in questa pagina." : null);
                if (versions.Items.Count > 0) versionPicker.SelectedIndex = 0;
                long totalPages = Math.Max(1L,
                    (versions.TotalCount + VersionPageSize - 1L) / VersionPageSize);
                versionsPageInfo.Text = "Pagina " + page + " / " + totalPages;
                previousVersionsButton.IsEnabled = page > 1;
                nextVersionsButton.IsEnabled = versions.HasNextPage;
                long first = versions.Items.Count == 0 ? 0 :
                    (long)(page - 1) * VersionPageSize + 1;
                long last = versions.Items.Count == 0 ? 0 :
                    first + versions.Items.Count - 1;
                versionInfo.Text = "Versioni " + first + "–" + last +
                    " di " + versions.TotalCount + " · " + VersionPageSize + " per pagina";
            }
            catch (OperationCanceledException)
            {
                if (generation == versionsGeneration)
                {
                    ShowVersionPlaceholder("Caricamento annullato.");
                    versionInfo.Text = "";
                    versionsPageInfo.Text = "";
                }
            }
            catch (Exception ex) when (ex is JavaMavenCentralSearchException ||
                ex is ArgumentException || ex is ArgumentOutOfRangeException)
            {
                if (generation == versionsGeneration)
                {
                    ShowVersionPlaceholder("Versioni temporaneamente non disponibili.");
                    versionInfo.Text = ex.Message;
                    versionsPageInfo.Text = "Pagina " + page + " · errore";
                    retryVersionsButton.Visibility = Visibility.Visible;
                }
            }
        }

        private async Task LoadUpdatesAsync()
        {
            RefreshInstalled();
            if (installed == null) return;
            var items = installed.Items.Where(x => x.MavenCoordinate &&
                !x.Version.StartsWith("(", StringComparison.Ordinal))
                .Take(25).ToList();
            if (items.Count == 0)
            {
                updatesNote.Text = "Nessuna dipendenza Maven Central con coordinate dirette.";
                updatesResults.ItemsSource = null;
                return;
            }
            int generation = ++updatesGeneration;
            ReplaceToken(ref updatesCts);
            var token = updatesCts.Token;
            updateButton.IsEnabled = false;
            updatesNote.Text = "Controllo in corso (max 25 dipendenze dirette)...";
            try
            {
                var candidates = new List<JavaPackageUpdateCandidate>();
                var failures = 0;
                // Sequential requests avoid rate-limit spikes and respect cancellation.
                foreach (var dep in items)
                {
                    token.ThrowIfCancellationRequested();
                    JavaCentralArtifact artifact;
                    try { artifact = await service.GetArtifactAsync(dep.GroupId, dep.ArtifactId, token); }
                    catch (Exception ex) when (ex is JavaMavenCentralSearchException ||
                        ex is ArgumentException) { failures++; continue; }
                    if (artifact != null && !string.IsNullOrWhiteSpace(artifact.LatestVersion) &&
                        !string.Equals(dep.Version, artifact.LatestVersion, StringComparison.Ordinal))
                        candidates.Add(new JavaPackageUpdateCandidate(dep, artifact.LatestVersion));
                }
                if (generation != updatesGeneration || token.IsCancellationRequested) return;
                updatesResults.ItemsSource = candidates;
                updatesNote.Text = candidates.Count + " versioni differenti trovate. " +
                    (failures > 0 ? failures + " verifiche non riuscite. " : "") +
                    "Non e' un confronto semantico: Maven Central puo' restituire " +
                    "versioni pre-release o inferiori. Nessuna modifica al progetto.";
            }
            catch (OperationCanceledException)
            { if (generation == updatesGeneration) updatesNote.Text = "Verifica annullata."; }
            finally { if (generation == updatesGeneration) updateButton.IsEnabled = true; }
        }

        private void CancelAll()
        {
            ++searchGeneration; ++versionsGeneration; ++updatesGeneration;
            Cancel(ref searchCts); Cancel(ref versionsCts); Cancel(ref updatesCts);
            SetSearchBusy(false);
            updateButton.IsEnabled = true;
        }
        private static void Cancel(ref CancellationTokenSource source)
        {
            if (source == null) return;
            source.Cancel();
            source.Dispose();
            source = null;
        }
        private static void ReplaceToken(ref CancellationTokenSource source)
        { Cancel(ref source); source = new CancellationTokenSource(); }
        private void SetSearchBusy(bool busy)
        {
            searchButton.IsEnabled = !busy;
            cancelButton.IsEnabled = busy;
            previousButton.IsEnabled = !busy && lastPage != null && currentPage > 1;
            nextButton.IsEnabled = !busy && lastPage != null && lastPage.HasNextPage;
        }
        // F4F21-UI-THEME: WPF's stock item presenter can lose the VS dark
        // Foreground. Bind text to the item's own themed brush explicitly.
        private static void DecorateList(ListBox list, string property)
        {
            list.Padding = new Thickness(4, 4, 4, 4);
            ThemeControl(list);
            // F4.F.2.3: DisplayMemberPath and ItemTemplate are mutually exclusive in WPF.
            list.ClearValue(ItemsControl.DisplayMemberPathProperty);
            list.ItemTemplate = ListItemTemplate(property);

            var chrome = new FrameworkElementFactory(typeof(Border));
            chrome.Name = "PackageItemChrome";
            chrome.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 1));
            chrome.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
            chrome.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.MarginProperty, new Thickness(7, 6, 7, 6));
            chrome.AppendChild(presenter);
            var template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = chrome };
            var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Border.BorderBrushProperty,
                new SolidColorBrush(Color.FromRgb(0, 122, 204)), "PackageItemChrome"));
            // Keep readable foreground even when selection loses keyboard focus.
            selected.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
            template.Triggers.Add(selected);
            var style = new Style(typeof(ListBoxItem));
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,
                HorizontalAlignment.Stretch));
            list.ItemContainerStyle = style;
        }

        private static DataTemplate ListItemTemplate(string property)
        {
            var factory = new FrameworkElementFactory(typeof(TextBlock));
            factory.SetBinding(TextBlock.TextProperty,
                string.IsNullOrEmpty(property) ? new System.Windows.Data.Binding() :
                    new System.Windows.Data.Binding(property));
            factory.SetResourceReference(TextBlock.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            factory.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            factory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            return new DataTemplate { VisualTree = factory };
        }

        // F4.F.2.4: NuGet-inspired flat navigation, without relying on NuGet's
        // internal/private controls. TabControl and TabItem remain standard WPF
        // for keyboard, selection, and automation; colors follow VS resources.
        private static void ApplyNuGetLikeTabs(TabControl control)
        {
            var root = new FrameworkElementFactory(typeof(DockPanel));
            root.SetValue(DockPanel.LastChildFillProperty, true);
            root.SetResourceReference(DockPanel.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);

            var headers = new FrameworkElementFactory(typeof(TabPanel));
            headers.Name = "HeaderPanel";
            headers.SetValue(Panel.IsItemsHostProperty, true);
            headers.SetValue(DockPanel.DockProperty, Dock.Top);
            headers.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
            root.AppendChild(headers);

            var separator = new FrameworkElementFactory(typeof(Border));
            separator.SetValue(DockPanel.DockProperty, Dock.Top);
            separator.SetValue(FrameworkElement.HeightProperty, 1.0);
            separator.SetResourceReference(Border.BackgroundProperty,
                EnvironmentColors.ToolWindowBorderBrushKey);
            root.AppendChild(separator);

            var selectedContent = new FrameworkElementFactory(typeof(ContentPresenter));
            selectedContent.Name = "PART_SelectedContentHost";
            selectedContent.SetValue(ContentPresenter.ContentSourceProperty, "SelectedContent");
            selectedContent.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
            root.AppendChild(selectedContent);
            control.Template = new ControlTemplate(typeof(TabControl)) { VisualTree = root };

            var tabRoot = new FrameworkElementFactory(typeof(Grid));
            tabRoot.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 4, 0));
            var body = new FrameworkElementFactory(typeof(Border));
            body.Name = "JavaNuGetTabBody";
            body.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            body.SetValue(Border.PaddingProperty, new Thickness(15, 8, 15, 10));
            var caption = new FrameworkElementFactory(typeof(ContentPresenter));
            caption.SetBinding(ContentPresenter.ContentProperty,
                new System.Windows.Data.Binding("Header")
                { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            caption.SetBinding(ContentPresenter.ContentTemplateProperty,
                new System.Windows.Data.Binding("HeaderTemplate")
                { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            caption.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            body.AppendChild(caption);
            tabRoot.AppendChild(body);
            var underline = new FrameworkElementFactory(typeof(Border));
            underline.Name = "JavaNuGetTabUnderline";
            underline.SetValue(FrameworkElement.HeightProperty, 2.0);
            underline.SetValue(FrameworkElement.VerticalAlignmentProperty,
                VerticalAlignment.Bottom);
            underline.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            tabRoot.AppendChild(underline);

            var tabTemplate = new ControlTemplate(typeof(TabItem)) { VisualTree = tabRoot };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush(Color.FromArgb(22, 0, 122, 204)), "JavaNuGetTabBody"));
            tabTemplate.Triggers.Add(hover);
            var active = new Trigger { Property = TabItem.IsSelectedProperty, Value = true };
            active.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush(Color.FromRgb(0, 122, 204)), "JavaNuGetTabUnderline"));
            active.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
            tabTemplate.Triggers.Add(active);

            var tabStyle = new Style(typeof(TabItem));
            tabStyle.Setters.Add(new Setter(Control.TemplateProperty, tabTemplate));
            tabStyle.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
            tabStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            control.ItemContainerStyle = tabStyle;
            foreach (var item in control.Items.OfType<TabItem>())
                item.Style = tabStyle;
        }

        private void ShowVersionPlaceholder(string message)
        {
            versionEmptyState.Text = message ?? string.Empty;
            versionEmptyState.Visibility = string.IsNullOrWhiteSpace(message)
                ? Visibility.Collapsed : Visibility.Visible;
        }

        private static TabItem ThemedTab(string title, FrameworkElement content)
        {
            var surface = new Border { Child = content };
            surface.SetResourceReference(Border.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            var tab = new TabItem { Header = title, Content = surface };
            ThemeControl(tab);
            return tab;
        }

        private static void ThemeBackground(Panel panel)
        {
            panel.SetResourceReference(Panel.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
        }

        private static void ThemeControl(Control control)
        {
            control.SetResourceReference(Control.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            control.SetResourceReference(Control.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            control.SetResourceReference(Control.BorderBrushProperty,
                EnvironmentColors.ToolWindowBorderBrushKey);
        }
        private static Button ActionButton(string text)
        {
            var button = new Button { Content = text,
                Padding = new Thickness(12, 5, 12, 5), MinHeight = 27 };
            JavaNavigationWindowStyle.ThemeNavigationActionButton(button);
            return button;
        }
        private static TextBlock Heading(string text, double size)
        {
            var heading = new TextBlock { Text = text, FontSize = size,
                FontWeight = FontWeights.SemiBold };
            heading.SetResourceReference(TextBlock.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            return heading;
        }
        private static TextBlock CaptionText(string text)
        {
            var label = new TextBlock { Text = text, FontSize = 12,
                TextWrapping = TextWrapping.Wrap };
            label.SetResourceReference(TextBlock.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            return label;
        }
    }
}
