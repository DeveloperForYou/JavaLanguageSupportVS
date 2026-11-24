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
    // JPM-S1-SCOPES-20261123

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

    // F4F291-RESPONSIVE-VERSION-ORIGINS: immutable UI projection.
    // Keep JavaCentralVersion and the repository catalog unmodified.
    internal sealed class JavaPackageVersionDisplay
    {
        internal JavaPackageVersionDisplay(JavaCentralVersion version, string origins)
        {
            Version = version.Version;
            Origins = string.IsNullOrWhiteSpace(origins) ? "Origine non indicata" : origins;
        }
        public string Version { get; private set; }
        public string Origins { get; private set; }
        public string OriginSummary { get { return "Disponibile su: " + Origins; } }
        public string FullDescription { get { return Version + "\n" + OriginSummary; } }
    }

    // F4F210-SOURCE-PICKER: presentation-only choice, never persists secrets or settings.
    internal sealed class JavaPackageSourceChoice
    {
        internal JavaPackageSourceChoice(JavaPackageSource source, string label)
        { Source = source; DisplayName = label; }
        internal JavaPackageSource Source { get; private set; }
        public string DisplayName { get; private set; }
    }

    // Simple public binding properties; the popup never changes packages.
    internal sealed class JavaCoordinateSuggestion
    {
        internal JavaCoordinateSuggestion(string coordinate, string origin)
        { Coordinate = coordinate; Origin = origin; }
        public string Coordinate { get; private set; }
        public string Origin { get; private set; }
        public string Display { get { return Coordinate + "  ·  " + Origin; } }
        public string ArtifactName
        {
            get
            {
                int separator = Coordinate.IndexOf(':');
                return separator < 0 ? Coordinate : Coordinate.Substring(separator + 1);
            }
        }
    }

    internal sealed class JavaPackageManagerControl : UserControl
    {
        private readonly JavaMavenCentralSearchService service = new JavaMavenCentralSearchService();
        // F4F29-MULTI-REPOSITORY: active origins, metadata lookup, deduplication.
        private readonly JavaPackageMultiRepositorySearchService repositoryService =
            new JavaPackageMultiRepositorySearchService();
        private JavaRepositoryCatalogResult lastCatalog;
        private readonly TextBlock projectHeader = Heading("Seleziona un progetto Java", 17);
        private readonly TextBlock projectInfo = CaptionText("Ricerca su Maven Central · sola lettura");
        private readonly TextBlock status = CaptionText("Pronto.");
        // F4F284-PROJECT-TARGET: explicit per-solution selection, never follow editor focus.
        private readonly ComboBox projectSelector = new ComboBox { MinWidth = 125, MaxWidth = 680,
            VerticalContentAlignment = VerticalAlignment.Center };
        private readonly Button refreshProjectsButton = ActionButton("Aggiorna");
        private bool suppressProjectSelection;
        // F4F28-PACKAGE-SOURCES: native configuration, read-only discovery.
        // F4F2102-ALIGNED-PICKERS: only the selector is visible; discovery
        // diagnostics remain accessible by hovering over its refresh button.
        private readonly Button discoverSourcesButton = ActionButton("Rileva origini");
        private CancellationTokenSource sourcesCts;
        private int sourcesGeneration;
        private JavaPackageSourceSnapshot packageSources;
        private readonly ComboBox sourceSelector = new ComboBox { MinWidth = 120,
            MaxWidth = 680, VerticalContentAlignment = VerticalAlignment.Center };
        private bool suppressSourceSelection;
        // null means all effective HTTPS origins; otherwise an exact endpoint URL.
        private string selectedSourceUrl;
        private readonly TextBox searchBox = new TextBox();
        private readonly Popup suggestionPopup = new Popup();
        private readonly ListBox suggestionList = new ListBox();
        private readonly TextBlock suggestionHint = CaptionText("");
        private readonly CheckBox publicSuggestions = new CheckBox();
        private readonly List<string> recentCoordinates = new List<string>();
        private CancellationTokenSource suggestionCts;
        private int suggestionGeneration;
        private bool acceptingSuggestion;
        private bool resettingSuggestions;
        private readonly Button searchButton = ActionButton("Cerca");
        private readonly Button cancelButton = ActionButton("Annulla");
        private readonly Button previousButton = ActionButton("◀ Precedente");
        private readonly Button nextButton = ActionButton("Successiva ▶");
        private readonly TextBlock pageInfo = CaptionText("");
        private readonly ListBox browseResults = new ListBox { DisplayMemberPath = "Coordinate" };
        private readonly TextBlock details = CaptionText("Seleziona una libreria per vedere i dettagli.");
        private readonly ListBox versionPicker = new ListBox(); // F4F21-THEMED-VERSIONS
        private readonly TextBlock versionInfo = CaptionText("");
        private readonly TextBlock versionOriginInfo = CaptionText("");
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
        // F4F2111-DIRECT-DEPENDENCIES: Maven/Gradle direct declarations only.
        private readonly Button installDependencyButton = ActionButton("Installa...");
        private readonly Button removeDependencyButton = ActionButton("Rimuovi...");
        private readonly Button applyUpdateButton = ActionButton("Aggiorna selezionato...");
        // JPM-S1: scope selection and edit. Use the existing VS-themed picker.
        private readonly ComboBox installScopeSelector = new ComboBox { Width = 108 };
        private readonly ComboBox installedScopeSelector = new ComboBox { Width = 108 };
        private readonly Button changeScopeButton = ActionButton("Modifica scope...");
        // JPM-LOG-20261124: per-project VS Output pane, disk log and opt-in build tool transcript.
        private readonly Button jpmOpenOutputButton = ActionButton("Output JPM");
        private readonly Button jpmToolLogButton = ActionButton("Log tool");
        private bool jpmToolBusy;
        // JPM-S2A-CONFLICT-GUARD-20261124
        // JPM-S2B-AUTO-AUDIT-20261124
        private readonly Button plainConflictsButton = ActionButton("Verifica conflitti Plain");
        private readonly TextBlock plainConflictsIndicator = CaptionText("Conflitti Plain: non verificati.");
        private readonly SemaphoreSlim plainConflictAuditGate = new SemaphoreSlim(1, 1);
        private int plainConflictAuditRevision;
        private bool auditingPlainConflicts;
        // JPM-S2C-20261124: explorer isolato per progetto Plain, sola lettura.
        private readonly JavaPlainConflictExplorerControl plainConflictExplorer =
            new JavaPlainConflictExplorerControl();
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

            // F4F293-DETAILS-ON-DEMAND: do not repeat the project name above its selector.
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 7) };
            // Share both label and button column widths across these two rows.
            Grid.SetIsSharedSizeScope(header, true);
            projectHeader.Visibility = Visibility.Collapsed;
            header.Children.Add(projectHeader);
            var projectRow = new Grid { Margin = new Thickness(0, 5, 0, 3) };
            projectRow.ColumnDefinitions.Add(new ColumnDefinition {
                Width = GridLength.Auto, SharedSizeGroup = "JavaPickerLabels" });
            projectRow.ColumnDefinitions.Add(new ColumnDefinition {
                Width = new GridLength(1, GridUnitType.Star) });
            projectRow.ColumnDefinitions.Add(new ColumnDefinition {
                Width = GridLength.Auto, SharedSizeGroup = "JavaPickerButtons" });
            var projectLabel = CaptionText("Progetto:");
            projectLabel.VerticalAlignment = VerticalAlignment.Center;
            projectLabel.Margin = new Thickness(0, 0, 8, 0);
            projectRow.Children.Add(projectLabel);
            ThemeProjectSelector(projectSelector);
            projectSelector.ToolTip = "Progetto Java destinatario delle operazioni del Package Manager";
            projectSelector.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(projectSelector, 1);
            projectRow.Children.Add(projectSelector);
            refreshProjectsButton.ToolTip = "Rileggi i progetti Java caricati nella soluzione";
            Grid.SetColumn(refreshProjectsButton, 2);
            projectRow.Children.Add(refreshProjectsButton);
            header.Children.Add(projectRow);
            // Use the already proven dark/light ComboBox template of the project picker.
            var sourceRow = new Grid { Margin = new Thickness(0, 1, 0, 3) };
            sourceRow.ColumnDefinitions.Add(new ColumnDefinition {
                Width = GridLength.Auto, SharedSizeGroup = "JavaPickerLabels" });
            sourceRow.ColumnDefinitions.Add(new ColumnDefinition {
                Width = new GridLength(1, GridUnitType.Star) });
            sourceRow.ColumnDefinitions.Add(new ColumnDefinition {
                Width = GridLength.Auto, SharedSizeGroup = "JavaPickerButtons" });
            var sourceLabel = CaptionText("Origine:");
            sourceLabel.VerticalAlignment = VerticalAlignment.Center;
            sourceLabel.Margin = new Thickness(0, 0, 8, 0);
            sourceRow.Children.Add(sourceLabel);
            ThemeProjectSelector(sourceSelector);
            sourceSelector.IsEnabled = false;
            sourceSelector.ToolTip = "Scegli l'origine per ricerca, versioni e aggiornamenti. " +
                "I repository Gradle inattivi o HTTP non sono selezionabili.";
            sourceSelector.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(sourceSelector, 1);
            sourceRow.Children.Add(sourceSelector);
            discoverSourcesButton.IsEnabled = false;
            discoverSourcesButton.ToolTip = "Rileva i repository configurati per il progetto.";
            ToolTipService.SetShowDuration(discoverSourcesButton, 30000);
            Grid.SetColumn(discoverSourcesButton, 2);
            sourceRow.Children.Add(discoverSourcesButton);
            header.Children.Add(sourceRow);
            projectInfo.Margin = new Thickness(0, 3, 0, 0);
            header.Children.Add(projectInfo);
            discoverSourcesButton.Click += (s, e) =>
            {
                if (sourcesCts != null) StopSourceDiscovery();
                else _ = DiscoverSourcesAsync();
            };
            root.Children.Add(header);

            ThemeProjectSelector(installScopeSelector);
            ThemeProjectSelector(installedScopeSelector);
            // The project picker template binds DisplayName; scope items are strings.
            installScopeSelector.ItemTemplate = ListItemTemplate(null);
            installedScopeSelector.ItemTemplate = ListItemTemplate(null);
            installScopeSelector.ItemsSource = JavaPackageScopes.Supported;
            installedScopeSelector.ItemsSource = JavaPackageScopes.Supported;
            installScopeSelector.SelectedItem = "compile";
            installedScopeSelector.SelectedItem = "compile";
            installScopeSelector.ToolTip = "Scope della nuova dipendenza: compile, provided, runtime, test";
            installedScopeSelector.ToolTip = "Nuovo scope della dipendenza selezionata";
            ThemeControl(tabs);
            tabs.Items.Add(ThemedTab("Sfoglia", MakeBrowse()));
            tabs.Items.Add(ThemedTab("Installati", MakeInstalled()));
            tabs.Items.Add(ThemedTab("Aggiornamenti", MakeUpdates()));
            tabs.Items.Add(ThemedTab("Conflitti Plain", plainConflictExplorer));
            ApplyNuGetLikeTabs(tabs); // F4F24_NUGET_TABS
            Grid.SetRow(tabs, 1);
            root.Children.Add(tabs);

            var statusPanel = new Border { Padding = new Thickness(2, 8, 2, 2), Child = status };
            var jpmLogFooter = new DockPanel();
            jpmOpenOutputButton.Margin = new Thickness(6, 4, 0, 0);
            jpmOpenOutputButton.ToolTip = "Apri Visualizza > Output, sezione JPM del progetto selezionato";
            DockPanel.SetDock(jpmOpenOutputButton, Dock.Right);
            jpmLogFooter.Children.Add(jpmOpenOutputButton);
            jpmToolLogButton.Margin = new Thickness(6, 4, 0, 0);
            jpmToolLogButton.ToolTip = "Esegui la diagnostica delle dipendenze Maven/Gradle in sola lettura; log in Output e su disco";
            DockPanel.SetDock(jpmToolLogButton, Dock.Right);
            jpmLogFooter.Children.Add(jpmToolLogButton);
            jpmLogFooter.Children.Add(statusPanel);
            Grid.SetRow(jpmLogFooter, 2);
            root.Children.Add(jpmLogFooter);
            Content = root;
            MinWidth = 320;
            MinHeight = 310;

            projectSelector.SelectionChanged += (s, e) =>
            {
                if (suppressProjectSelection) return;
                var choice = projectSelector.SelectedItem as JavaPackageProjectEntry;
                if (choice != null) ChangeTargetProject(choice.ProjectFile);
            };
            projectSelector.DropDownOpened += (s, e) => RefreshProjectChoices(null, false);
            refreshProjectsButton.Click += (s, e) => RefreshProjectChoices(null, false);
            Loaded += (s, e) => { if (projectFile == null) RefreshProjectChoices(null, false); };
            sourceSelector.SelectionChanged += (s, e) =>
            {
                if (suppressSourceSelection || packageSources == null) return;
                var choice = sourceSelector.SelectedItem as JavaPackageSourceChoice;
                string url = choice == null || choice.Source == null
                    ? null : choice.Source.Url.AbsoluteUri.TrimEnd('/');
                if (string.Equals(selectedSourceUrl, url, StringComparison.OrdinalIgnoreCase)) return;
                selectedSourceUrl = url;
                bool hadSearch = !string.IsNullOrWhiteSpace(query);
                ResetSourceBoundResults();
                status.Text = "Origine selezionata: " + (choice == null ? "Tutte le origini attive" : choice.DisplayName);
                // Only repeat a search that the user previously executed.
                if (hadSearch && !string.IsNullOrWhiteSpace(searchBox.Text))
                    _ = StartSearchAsync(1);
            };
            searchBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; _ = StartSearchAsync(1); }
            };
            searchButton.Click += (s, e) => { _ = StartSearchAsync(1); };
            cancelButton.Click += (s, e) => { CancelAll(); status.Text = "Richiesta annullata."; };
            previousButton.Click += (s, e) => { if (currentPage > 1) _ = StartSearchAsync(currentPage - 1); };
            nextButton.Click += (s, e) => { if (lastPage != null && lastPage.HasNextPage) _ = StartSearchAsync(currentPage + 1); };
            browseResults.SelectionChanged += (s, e) => { RefreshDependencyActions(); _ = LoadVersionsAsync(1, true); };
            versionPicker.SelectionChanged += (s, e) =>
            {
                var picked = versionPicker.SelectedItem as JavaPackageVersionDisplay;
                versionOriginInfo.Text = picked == null ? "" : picked.OriginSummary;
                RefreshDependencyActions();
            };
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
                    "\nScope: " + dep.Scope +
                    "\nOrigine: " + (installed == null ? "-" : installed.Backend);
                installedScopeSelector.SelectedItem = dep != null && JavaPackageScopes.IsSupported(dep.Scope)
                    ? dep.Scope : "compile";
                RefreshDependencyActions();
            };
            updateButton.Click += (s, e) => { _ = LoadUpdatesAsync(); };
            installDependencyButton.Click += (s, e) => ApplyDependencyChange(JavaDependencyOperation.Install);
            removeDependencyButton.Click += (s, e) => ApplyDependencyChange(JavaDependencyOperation.Remove);
            applyUpdateButton.Click += (s, e) => ApplyDependencyChange(JavaDependencyOperation.Update);
            installedScopeSelector.SelectionChanged += (s, e) => RefreshDependencyActions();
            changeScopeButton.Click += (s, e) => ApplyInstalledScopeChange();
            jpmOpenOutputButton.Click += (s, e) => JavaJpmProjectLogger.Open(projectFile);
            jpmToolLogButton.Click += (s, e) => { _ = RunJpmToolLogAsync(); };
            plainConflictsButton.Click += (s, e) => { _ = AuditPlainConflictsAsync(); };
            updatesResults.SelectionChanged += (s, e) => RefreshDependencyActions();
            RefreshDependencyActions();
            retryVersionsButton.Click += (s, e) => { _ = LoadVersionsAsync(requestedVersionsPage, false); };
            tabs.SelectionChanged += (s, e) =>
            {
                if (e.Source == tabs && tabs.SelectedIndex == 1) RefreshInstalled();
                if (e.Source == tabs && tabs.SelectedIndex != 0) StopSuggestions();
            };
            Unloaded += (s, e) => CancelAll();
            SetSearchBusy(false);
            ResetVersionsPaging();
            versionPicker.IsHitTestVisible = false;
            ShowVersionPlaceholder("Seleziona una libreria per visualizzare le versioni.");
        }

        private FrameworkElement MakeBrowse()
        {
            var grid = new Grid { Margin = new Thickness(8, 6, 8, 6) };
            ThemeBackground(grid);
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var toolbar = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            searchBox.Margin = new Thickness(0, 0, 8, 0);
            searchBox.Padding = new Thickness(8, 4, 8, 4);
            searchBox.ToolTip = "Cerca nome libreria, groupId, artifactId o coordinate. " +
                "Da 3 caratteri: suggerimenti; ↑ ↓ scorrono, Invio cerca, Tab completa, Esc chiude.";
            searchBox.Text = "";
            searchBox.SetResourceReference(Control.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            searchBox.SetResourceReference(Control.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            toolbar.Children.Add(searchBox);
            System.Windows.Automation.AutomationProperties.SetName(searchBox, "Ricerca pacchetti Java");
            Grid.SetColumn(searchButton, 1);
            searchButton.Margin = new Thickness(0, 0, 8, 0);
            toolbar.Children.Add(searchButton);
            Grid.SetColumn(cancelButton, 2);
            toolbar.Children.Add(cancelButton);
            ConfigureSuggestions(toolbar);
            grid.Children.Add(toolbar);

            var content = new Grid();
            // Responsive split: two columns when wide, vertically stacked on narrow docks.
            // Do not impose column MinWidth values that exceed the docked toolwindow width.
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.8, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(7) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3.2, GridUnitType.Star) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
            DecorateList(browseResults, "Coordinate");
            content.Children.Add(browseResults);
            // F4F292-ADAPTIVE-DETAILS-LAYOUT: a visible, draggable divider.
            var splitter = new GridSplitter
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Width = 6,
                ResizeDirection = GridResizeDirection.Columns,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext
            };
            splitter.SetResourceReference(Control.BackgroundProperty,
                EnvironmentColors.ToolWindowBorderBrushKey);
            Grid.SetColumn(splitter, 1);
            content.Children.Add(splitter);

            // F4F293-DETAILS-ON-DEMAND: prioritize the version list. Metadata
            // is available on demand rather than clipped into a 38-DIP strip.
            var right = new Grid { Margin = new Thickness(12, 0, 0, 0) };
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var versionHeader = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            versionHeader.ColumnDefinitions.Add(new ColumnDefinition {
                Width = new GridLength(1, GridUnitType.Star) });
            versionHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var versionsHeading = Heading("Versioni disponibili e origini", 13);
            versionsHeading.VerticalAlignment = VerticalAlignment.Center;
            versionsHeading.TextTrimming = TextTrimming.CharacterEllipsis;
            versionHeader.Children.Add(versionsHeading);
            var detailsToggle = ActionButton("Dettagli ▾");
            detailsToggle.ToolTip = "Mostra o nasconde i metadati completi del pacchetto";
            detailsToggle.Margin = new Thickness(9, 0, 0, 0);
            detailsToggle.Padding = new Thickness(8, 3, 8, 3);
            // Install only the selected version in the selected project.
            var versionActions = new StackPanel { Orientation = Orientation.Horizontal };
            installDependencyButton.Margin = new Thickness(0);
            installDependencyButton.ToolTip = "Aggiunge la versione selezionata al progetto Maven/Gradle";
            var installScopeLabel = CaptionText("Scope:");
            installScopeLabel.VerticalAlignment = VerticalAlignment.Center;
            installScopeLabel.Margin = new Thickness(0, 0, 5, 0);
            versionActions.Children.Add(installScopeLabel);
            installScopeSelector.Margin = new Thickness(0, 0, 8, 0);
            versionActions.Children.Add(installScopeSelector);
            versionActions.Children.Add(installDependencyButton);
            versionActions.Children.Add(detailsToggle);
            Grid.SetColumn(versionActions, 1);
            versionHeader.Children.Add(versionActions);
            Grid.SetRow(versionHeader, 0);
            right.Children.Add(versionHeader);

            details.TextWrapping = TextWrapping.Wrap;
            details.Margin = new Thickness(0);
            details.SetBinding(FrameworkElement.ToolTipProperty,
                new System.Windows.Data.Binding("Text") { Source = details });
            var detailsScroll = new ScrollViewer
            {
                Content = details,
                MaxHeight = 112,
                Margin = new Thickness(0, 0, 0, 8),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Visibility = Visibility.Collapsed
            };
            Grid.SetRow(detailsScroll, 1);
            right.Children.Add(detailsScroll);
            detailsToggle.Click += (sender, args) =>
            {
                bool show = detailsScroll.Visibility != Visibility.Visible;
                detailsScroll.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                detailsToggle.Content = show ? "Dettagli ▴" : "Dettagli ▾";
            };
            // Manual toggle: details remain accessible even in a short dock.
            // The list automatically gets back its space when details are closed.

            var versionHost = new Grid { MinHeight = 0, Margin = new Thickness(0, 0, 0, 3) };
            versionPicker.Margin = new Thickness(0);
            versionPicker.IsEnabled = true;
            DecorateList(versionPicker, null);
            versionPicker.ItemTemplate = VersionRowTemplate();
            versionPicker.ToolTip = "Ogni versione indica le origini che la contengono";
            ScrollViewer.SetVerticalScrollBarVisibility(versionPicker, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(versionPicker, ScrollBarVisibility.Disabled);
            VirtualizingPanel.SetIsVirtualizing(versionPicker, true);
            VirtualizingPanel.SetVirtualizationMode(versionPicker, VirtualizationMode.Recycling);
            versionHost.Children.Add(versionPicker);
            versionEmptyState.Margin = new Thickness(15, 12, 15, 12);
            versionEmptyState.VerticalAlignment = VerticalAlignment.Center;
            versionEmptyState.HorizontalAlignment = HorizontalAlignment.Center;
            versionEmptyState.TextAlignment = TextAlignment.Center;
            versionEmptyState.IsHitTestVisible = false;
            versionHost.Children.Add(versionEmptyState);
            Grid.SetRow(versionHost, 2);
            right.Children.Add(versionHost);

            var versionsPager = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 3, 0, 2)
            };
            previousVersionsButton.Margin = new Thickness(0, 0, 8, 0);
            versionsPageInfo.Margin = new Thickness(0, 0, 8, 0);
            versionsPageInfo.VerticalAlignment = VerticalAlignment.Center;
            previousVersionsButton.ToolTip = "Pagina precedente delle versioni";
            nextVersionsButton.ToolTip = "Pagina successiva delle versioni";
            versionsPager.Children.Add(previousVersionsButton);
            versionsPager.Children.Add(versionsPageInfo);
            versionsPager.Children.Add(nextVersionsButton);
            // Pager moved to the common bottom bar; versions keep all available height.

            // Status stays to one line: detailed warnings remain available in
            // a tooltip, while all remaining space belongs to version rows.
            versionInfo.TextWrapping = TextWrapping.NoWrap;
            versionInfo.TextTrimming = TextTrimming.CharacterEllipsis;
            versionInfo.VerticalAlignment = VerticalAlignment.Center;
            versionInfo.Margin = new Thickness(12, 0, 0, 0);
            versionInfo.SetBinding(FrameworkElement.ToolTipProperty,
                new System.Windows.Data.Binding("Text") { Source = versionInfo });
            // The version status joins its pager in the common bottom bar.
            retryVersionsButton.Margin = new Thickness(0, 3, 0, 0);
            retryVersionsButton.HorizontalAlignment = HorizontalAlignment.Left;
            retryVersionsButton.Visibility = Visibility.Collapsed;
            Grid.SetRow(retryVersionsButton, 3);
            right.Children.Add(retryVersionsButton);
            Grid.SetColumn(right, 2);
            content.Children.Add(right);
            Grid.SetRow(content, 1);
            grid.Children.Add(content);
            bool stacked = false;
            content.SizeChanged += (sender, args) =>
            {
                // Stacking two panes in a *short* dock produces two unusably
                // small lists. Stack only when there is enough vertical room.
                bool wantStacked = args.NewSize.Width < 700 && args.NewSize.Height >= 385;
                if (wantStacked == stacked) return;
                stacked = wantStacked;
                ApplyResponsiveBrowseLayout(content, browseResults, right, splitter, stacked);
            };

            var pager = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 6, 0, 0)
            };
            previousButton.Margin = new Thickness(0, 0, 10, 0);
            pageInfo.VerticalAlignment = VerticalAlignment.Center;
            pageInfo.Margin = new Thickness(0, 0, 10, 0);
            pager.Children.Add(previousButton);
            pager.Children.Add(pageInfo);
            pager.Children.Add(nextButton);

            // F4F294-SHARED-PAGERS: both page controls share one bottom row,
            // independent of the height of version metadata and versionInfo.
            var pagerBar = new Grid();
            pagerBar.ColumnDefinitions.Add(new ColumnDefinition {
                Width = new GridLength(1.8, GridUnitType.Star) });
            pagerBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            pagerBar.ColumnDefinitions.Add(new ColumnDefinition {
                Width = new GridLength(3.2, GridUnitType.Star) });
            pagerBar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            pagerBar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
            Grid.SetColumn(pager, 0);
            Grid.SetRow(pager, 0);
            pagerBar.Children.Add(pager);
            // F4F295-VERSION-STATUS-IN-PAGER: version range is on the SAME
            // row as the version navigation. The second column consumes only
            // remaining space and trims text (full content remains a tooltip).
            var versionPagerFooter = new Grid { Margin = new Thickness(12, 6, 0, 0) };
            versionPagerFooter.ColumnDefinitions.Add(new ColumnDefinition {
                Width = GridLength.Auto });
            versionPagerFooter.ColumnDefinitions.Add(new ColumnDefinition {
                Width = new GridLength(1, GridUnitType.Star) });
            versionsPager.Margin = new Thickness(0);
            versionsPager.VerticalAlignment = VerticalAlignment.Center;
            versionPagerFooter.Children.Add(versionsPager);
            Grid.SetColumn(versionInfo, 1);
            versionPagerFooter.Children.Add(versionInfo);
            Grid.SetColumn(versionPagerFooter, 2);
            Grid.SetRow(versionPagerFooter, 0);
            pagerBar.Children.Add(versionPagerFooter);
            Grid.SetRow(pagerBar, 2);
            grid.Children.Add(pagerBar);

            // Follow the actual left-column width, including splitter drags.
            // Narrow docks or a narrow left splitter pane use two pager rows.
            Action syncPagerLayout = () =>
            {
                bool narrow = grid.ActualWidth < 860 ||
                    (browseResults.ActualWidth > 0 && browseResults.ActualWidth < 285);
                pagerBar.RowDefinitions[1].Height = narrow ? GridLength.Auto : new GridLength(0);
                Grid.SetColumn(pager, 0);
                Grid.SetColumnSpan(pager, narrow ? 3 : 1);
                Grid.SetRow(pager, 0);
                Grid.SetColumn(versionPagerFooter, narrow ? 0 : 2);
                Grid.SetColumnSpan(versionPagerFooter, narrow ? 3 : 1);
                Grid.SetRow(versionPagerFooter, narrow ? 1 : 0);
                versionPagerFooter.Margin = narrow
                    ? new Thickness(0, 4, 0, 0)
                    : new Thickness(12, 6, 0, 0);
                if (narrow)
                {
                    pagerBar.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
                    pagerBar.ColumnDefinitions[1].Width = new GridLength(0);
                    pagerBar.ColumnDefinitions[2].Width = new GridLength(0);
                }
                else
                {
                    double actualLeft = browseResults.ActualWidth;
                    pagerBar.ColumnDefinitions[0].Width = actualLeft > 0
                        ? new GridLength(actualLeft, GridUnitType.Pixel)
                        : new GridLength(1.8, GridUnitType.Star);
                    pagerBar.ColumnDefinitions[1].Width = new GridLength(6);
                    pagerBar.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
                }
            };
            browseResults.SizeChanged += (sender, args) => syncPagerLayout();
            grid.SizeChanged += (sender, args) => syncPagerLayout();
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
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var installedHeader = new DockPanel();
            removeDependencyButton.Margin = new Thickness(9, 0, 0, 0);
            removeDependencyButton.ToolTip = "Rimuove la dipendenza diretta selezionata";
            DockPanel.SetDock(removeDependencyButton, Dock.Right);
            installedHeader.Children.Add(removeDependencyButton);
            changeScopeButton.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(changeScopeButton, Dock.Right);
            installedHeader.Children.Add(changeScopeButton);
            installedScopeSelector.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(installedScopeSelector, Dock.Right);
            installedHeader.Children.Add(installedScopeSelector);
            installedHeader.Children.Add(Heading("Dipendenze dichiarate nel progetto", 15));
            grid.Children.Add(installedHeader);
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
            // S2B: compact, theme-aware status; only counts in the UI, details in Output JPM.
            var conflictBar = new DockPanel { Margin = new Thickness(0, 7, 0, 0) };
            plainConflictsButton.HorizontalAlignment = HorizontalAlignment.Left;
            plainConflictsButton.Margin = new Thickness(0, 0, 10, 0);
            plainConflictsButton.ToolTip = "Verifica i JAR Plain gestiti; report completo in Output JPM";
            DockPanel.SetDock(plainConflictsButton, Dock.Left);
            conflictBar.Children.Add(plainConflictsButton);
            plainConflictsIndicator.VerticalAlignment = VerticalAlignment.Center;
            plainConflictsIndicator.TextWrapping = TextWrapping.Wrap;
            plainConflictsIndicator.Visibility = Visibility.Collapsed;
            conflictBar.Children.Add(plainConflictsIndicator);
            Grid.SetRow(conflictBar, 4);
            grid.Children.Add(conflictBar);
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
            var updateActions = new WrapPanel { Margin = new Thickness(0, 10, 0, 10) };
            updateButton.Margin = new Thickness(0, 0, 8, 0);
            updateActions.Children.Add(updateButton);
            applyUpdateButton.ToolTip = "Applica al progetto solo l'aggiornamento selezionato";
            updateActions.Children.Add(applyUpdateButton);
            Grid.SetRow(updateActions, 1);
            grid.Children.Add(updateActions);
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
            RefreshProjectChoices(newProjectFile, true);
            ChangeTargetProject(newProjectFile);
        }

        private void ChangeTargetProject(string newProjectFile)
        {
            if (string.IsNullOrWhiteSpace(newProjectFile)) return;
            if (string.Equals(projectFile, newProjectFile, StringComparison.OrdinalIgnoreCase))
            {
                RefreshInstalled();
                plainConflictExplorer.SetProject(projectFile, installed != null &&
                    string.Equals(installed.Backend, "Plain", StringComparison.OrdinalIgnoreCase));
                QueuePlainConflictAudit("selezione progetto");
                return;
            }
            CancelAll();
            // Cancel stale audit results when changing the target project.
            Interlocked.Increment(ref plainConflictAuditRevision);
            plainConflictsIndicator.Visibility = Visibility.Collapsed;
            projectFile = newProjectFile;
            JavaJpmProjectLogger.Info(projectFile, "JPM", "Progetto selezionato nel Java Package Manager.");
            JavaJpmProjectLogger.Open(projectFile);
            recentCoordinates.Clear();
            searchBox.Clear();
            packageSources = null;
            ResetSourceSelector();
            lastCatalog = null;
            versionOriginInfo.Text = "";
            discoverSourcesButton.ToolTip = "Rileva i repository configurati per il progetto.";
            discoverSourcesButton.IsEnabled = true;
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
            installedDetails.Text = "Seleziona una dipendenza.";
            projectHeader.Text = System.IO.Path.GetFileNameWithoutExtension(projectFile);
            RefreshInstalled();
            plainConflictExplorer.SetProject(projectFile, installed != null &&
                string.Equals(installed.Backend, "Plain", StringComparison.OrdinalIgnoreCase));
            QueuePlainConflictAudit("apertura progetto");
            tabs.SelectedIndex = 0;
            status.Text = "Rilevamento delle origini del progetto...";
            if (installed != null) _ = DiscoverSourcesAsync();
        }

        private void RefreshProjectChoices(string requested, bool fromProjectContext)
        {
            // Always enumerate the actual loaded solution projects, not folders on disk.
            var choices = JavaPackageManagerSolutionProjects.GetLoadedProjects(ServiceProvider.GlobalProvider)
                .ToList();
            string wanted = requested ?? projectFile;
            // The context-menu command is already validated by CPS. Keep it visible
            // even when the hierarchy is in the middle of loading into the solution.
            if (fromProjectContext && JavaPackageManagerSolutionProjects.Normalize(wanted) != null &&
                !choices.Any(p => string.Equals(p.ProjectFile, wanted, StringComparison.OrdinalIgnoreCase)))
                choices.Add(new JavaPackageProjectEntry(System.IO.Path.GetFullPath(wanted),
                    System.IO.Path.GetFileNameWithoutExtension(wanted)));
            var selected = choices.FirstOrDefault(p => string.Equals(
                p.ProjectFile, wanted, StringComparison.OrdinalIgnoreCase));
            suppressProjectSelection = true;
            try
            {
                projectSelector.ItemsSource = choices;
                projectSelector.SelectedItem = selected;
                projectSelector.IsEnabled = choices.Count != 0;
            }
            finally { suppressProjectSelection = false; }
            if (selected == null && !string.IsNullOrWhiteSpace(projectFile))
                ClearProjectTarget();
            if (choices.Count == 0 && string.IsNullOrWhiteSpace(projectFile))
                status.Text = "Nessun progetto .javaproj caricato. Apri un progetto Java nella soluzione.";
        }

        private void ClearProjectTarget()
        {
            CancelAll();
            Interlocked.Increment(ref plainConflictAuditRevision);
            plainConflictsIndicator.Visibility = Visibility.Collapsed;
            projectFile = null;
            plainConflictExplorer.SetProject(null, false);
            recentCoordinates.Clear();
            searchBox.Clear();
            installed = null;
            packageSources = null;
            ResetSourceSelector();
            lastCatalog = null;
            versionOriginInfo.Text = "";
            query = null;
            currentPage = 1;
            lastPage = null;
            installedResults.ItemsSource = null;
            installedDetails.Text = "Seleziona una dipendenza.";
            installedNote.Text = "";
            updatesResults.ItemsSource = null;
            updatesNote.Text = "Seleziona un progetto Java.";
            browseResults.ItemsSource = null;
            details.Text = "Seleziona una libreria per vedere i dettagli.";
            versionPicker.ItemsSource = null;
            versionPicker.IsHitTestVisible = false;
            ResetVersionsPaging();
            ShowVersionPlaceholder("Seleziona un progetto Java.");
            pageInfo.Text = "";
            versionInfo.Text = "";
            projectHeader.Text = "Seleziona un progetto Java";
            projectInfo.Text = "Nessun progetto selezionato";
            discoverSourcesButton.ToolTip = "Seleziona un progetto per rilevarne i repository.";
            discoverSourcesButton.IsEnabled = false;
            updateButton.IsEnabled = false;
            SetSearchBusy(false);
            status.Text = "Seleziona un progetto dall'elenco.";
        }

        // F4F2111-DIRECT-DEPENDENCIES: Never write on selection or during search.
        // Only a deliberately confirmed action can change the selected project.
        private void RefreshDependencyActions()
        {
            // F4F2112-PLAIN-MANAGEMENT: enable Plain only for package-managed coordinates.
            bool plain = installed != null && string.Equals(installed.Backend,
                "Plain", StringComparison.OrdinalIgnoreCase);
            plainConflictsButton.IsEnabled = plain && !auditingPlainConflicts && !plainPackageBusy;
            bool writable = !plainPackageBusy && installed != null &&
                (plain || string.Equals(installed.Backend, "Maven", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(installed.Backend, "Gradle", StringComparison.OrdinalIgnoreCase));
            var version = versionPicker.SelectedItem as JavaPackageVersionDisplay;
            var artifact = browseResults.SelectedItem as JavaCentralArtifact;
            installDependencyButton.IsEnabled = writable && artifact != null && version != null &&
                lastCatalog != null && lastCatalog.Versions.Contains(version.Version);
            var removable = installedResults.SelectedItem as JavaInstalledDependency;
            removeDependencyButton.IsEnabled = writable && removable != null &&
                (!plain || removable.MavenCoordinate);
            var candidate = updatesResults.SelectedItem as JavaPackageUpdateCandidate;
            applyUpdateButton.IsEnabled = writable && candidate != null &&
                (!plain || candidate.Installed.MavenCoordinate);
            installScopeSelector.IsEnabled = writable;
            var scopeItem = installedResults.SelectedItem as JavaInstalledDependency;
            changeScopeButton.IsEnabled = writable && scopeItem != null &&
                scopeItem.MavenCoordinate && JavaPackageScopes.IsSupported(scopeItem.Scope) &&
                !string.Equals(scopeItem.Scope, installedScopeSelector.SelectedItem as string,
                    StringComparison.OrdinalIgnoreCase);
            installedScopeSelector.IsEnabled = writable && scopeItem != null &&
                scopeItem.MavenCoordinate && JavaPackageScopes.IsSupported(scopeItem.Scope);
            jpmOpenOutputButton.IsEnabled = !string.IsNullOrWhiteSpace(projectFile);
            bool hasTool = installed != null && (string.Equals(installed.Backend,
                "Maven", StringComparison.OrdinalIgnoreCase) || string.Equals(installed.Backend,
                "Gradle", StringComparison.OrdinalIgnoreCase));
            jpmToolLogButton.Visibility = hasTool ? Visibility.Visible : Visibility.Collapsed;
            jpmToolLogButton.IsEnabled = hasTool && !jpmToolBusy && !plainPackageBusy;
        }

        private void ApplyInstalledScopeChange()
        {
            var selected = installedResults.SelectedItem as JavaInstalledDependency;
            string wanted = installedScopeSelector.SelectedItem as string;
            if (plainPackageBusy || installed == null || selected == null || !selected.MavenCoordinate ||
                !JavaPackageScopes.IsSupported(selected.Scope) ||
                !JavaPackageScopes.IsSupported(wanted) ||
                string.Equals(selected.Scope, wanted, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(projectFile)) return;
            if (string.Equals(installed.Backend, "Plain", StringComparison.OrdinalIgnoreCase))
            {
                _ = ApplyPlainDependencyChangeAsync(JavaDependencyOperation.ChangeScope);
                return;
            }
            string originalProject = projectFile;
            JavaJpmProjectLogger.Info(originalProject, "JPM", "Richiesta modifica scope " +
                selected.Coordinate + " da " + selected.Scope + " a " + wanted + ".");
            try
            {
                JavaDependencyChange change = JavaPackageDirectDependencyEditor.Plan(originalProject,
                    JavaDependencyOperation.ChangeScope, selected.GroupId,
                    selected.ArtifactId, null, wanted);
                string message = change.Summary + "\n\n" +
                    "La modifica verrà applicata al progetto con backup. Confermare?";
                if (MessageBox.Show(message, "JPM - Modifica scope", MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                if (!string.Equals(originalProject, projectFile, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Il progetto selezionato è cambiato.");
                change.Apply();
                JavaJpmProjectLogger.Info(originalProject, "JPM", change.Summary +
                    ". Backup: " + change.BackupPath);
                CancelAll();
                updatesResults.ItemsSource = null;
                RefreshInstalled();
                JavaLanguageSupport.ProjectSystem.Dependencies.JavaDependencyLanguageSync.RefreshAll();
                status.Text = "Scope modificato. Dettagli in Output JPM.";
                _ = RunJpmToolLogAsync();
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException ||
                ex is ArgumentException || ex is InvalidOperationException ||
                ex is NotSupportedException || ex is XmlException)
            {
                JavaJpmProjectLogger.Error(originalProject, "JPM scope", ex);
                status.Text = "Scope non modificato. Dettagli in Output JPM.";
            }
        }

        private void ApplyDependencyChange(JavaDependencyOperation operation)
        {
            if (string.IsNullOrWhiteSpace(projectFile) || installed == null) return;
            if (string.Equals(installed.Backend, "Plain", StringComparison.OrdinalIgnoreCase))
            {
                _ = ApplyPlainDependencyChangeAsync(operation);
                return;
            }
            string initialProject = projectFile;
            JavaJpmProjectLogger.Info(initialProject, "JPM", "Richiesta " + operation +
                " di una dipendenza diretta " + installed.Backend + ".");
            try
            {
                string group, artifact, version = null;
                if (operation == JavaDependencyOperation.Install)
                {
                    var found = browseResults.SelectedItem as JavaCentralArtifact;
                    var picked = versionPicker.SelectedItem as JavaPackageVersionDisplay;
                    if (found == null || picked == null || lastCatalog == null ||
                        !lastCatalog.Versions.Contains(picked.Version))
                        throw new InvalidOperationException("Selezionare una libreria e una versione caricata.");
                    group = found.GroupId; artifact = found.ArtifactId; version = picked.Version;
                }
                else if (operation == JavaDependencyOperation.Update)
                {
                    var candidate = updatesResults.SelectedItem as JavaPackageUpdateCandidate;
                    if (candidate == null) throw new InvalidOperationException("Selezionare un aggiornamento.");
                    group = candidate.Installed.GroupId;
                    artifact = candidate.Installed.ArtifactId;
                    version = candidate.Latest;
                }
                else
                {
                    var declared = installedResults.SelectedItem as JavaInstalledDependency;
                    if (declared == null || !declared.MavenCoordinate)
                        throw new InvalidOperationException("Selezionare una dipendenza diretta Maven/Gradle.");
                    group = declared.GroupId; artifact = declared.ArtifactId;
                }
                var change = JavaPackageDirectDependencyEditor.Plan(initialProject,
                    operation, group, artifact, version,
                    operation == JavaDependencyOperation.Install ?
                        (installScopeSelector.SelectedItem as string ?? "compile") : "compile");
                string message = change.Summary + "\n\nProgetto: " +
                    System.IO.Path.GetFileName(initialProject) +
                    "\nFile: " + change.FilePath +
                    "\n\nVerrà creata una copia di sicurezza prima del salvataggio." +
                    "\nConfermare la modifica della dipendenza diretta?";
                if (MessageBox.Show(message, "Gestione dipendenze Java",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                if (!string.Equals(initialProject, projectFile, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Il progetto selezionato è cambiato.");
                change.Apply();
                JavaJpmProjectLogger.Info(initialProject, "JPM", change.Summary +
                    ". Backup: " + change.BackupPath);
                CancelAll();
                updatesResults.ItemsSource = null;
                RefreshInstalled();
                RefreshDependencyActions();
                status.Text = "Dipendenza modificata. Dettagli in Output JPM.";
                _ = RunJpmToolLogAsync();
            }
            catch (Exception ex) when (ex is System.IO.IOException ||
                ex is UnauthorizedAccessException || ex is ArgumentException ||
                ex is NotSupportedException || ex is InvalidOperationException ||
                ex is XmlException)
            {
                JavaJpmProjectLogger.Error(initialProject, "JPM dipendenze", ex);
                status.Text = "Dipendenza non modificata. Dettagli in Output JPM.";
            }
        }

        // F4F2112-PLAIN-MANAGEMENT: dependency resolution is never run on the UI thread.
        // Resolve to an isolated temp folder, confirm exact JAR count, commit atomically.
        private bool plainPackageBusy;

        private async Task ApplyPlainDependencyChangeAsync(JavaDependencyOperation operation)
        {
            if (plainPackageBusy || string.IsNullOrWhiteSpace(projectFile) || installed == null) return;
            string initialProject = projectFile;
            JavaJpmProjectLogger.Info(initialProject, "JPM", "Richiesta " + operation +
                " su progetto Plain.");
            plainPackageBusy = true;
            RefreshDependencyActions();
            try
            {
                string group, artifact, version = null;
                if (operation == JavaDependencyOperation.Install)
                {
                    var found = browseResults.SelectedItem as JavaCentralArtifact;
                    var picked = versionPicker.SelectedItem as JavaPackageVersionDisplay;
                    if (found == null || picked == null || lastCatalog == null ||
                        !lastCatalog.Versions.Contains(picked.Version))
                        throw new InvalidOperationException("Selezionare una libreria e una versione Plain.");
                    group = found.GroupId; artifact = found.ArtifactId; version = picked.Version;
                }
                else if (operation == JavaDependencyOperation.Update)
                {
                    var candidate = updatesResults.SelectedItem as JavaPackageUpdateCandidate;
                    if (candidate == null || !candidate.Installed.MavenCoordinate)
                        throw new InvalidOperationException("Selezionare un pacchetto Plain gestito.");
                    group = candidate.Installed.GroupId;
                    artifact = candidate.Installed.ArtifactId;
                    version = candidate.Latest;
                }
                else
                {
                    var selected = installedResults.SelectedItem as JavaInstalledDependency;
                    if (selected == null || !selected.MavenCoordinate)
                        throw new InvalidOperationException("I JAR esterni non gestiti rimangono intatti.");
                    group = selected.GroupId; artifact = selected.ArtifactId;
                }
                if (operation == JavaDependencyOperation.ChangeScope)
                {
                    var selectedScopePackage = installedResults.SelectedItem as JavaInstalledDependency;
                    if (selectedScopePackage == null || !selectedScopePackage.MavenCoordinate)
                        throw new InvalidOperationException("Selezionare una dipendenza Plain gestita.");
                    version = selectedScopePackage.Version;
                }
                string candidateScope = null;
                if (operation == JavaDependencyOperation.Update)
                {
                    var selectedUpdate = updatesResults.SelectedItem as JavaPackageUpdateCandidate;
                    candidateScope = selectedUpdate == null ? null : selectedUpdate.Installed.Scope;
                }
                var origins = SelectedSources();
                status.Text = operation == JavaDependencyOperation.Remove ?
                    "Preparazione rimozione Plain..." :
                    operation == JavaDependencyOperation.ChangeScope ?
                        "Cambio scope e risoluzione transitive Plain in corso..." :
                        "Risoluzione Maven delle dipendenze transitive Plain in corso...";
                using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
                using (var change = await JavaPlainPackageManager.PrepareAsync(initialProject,
                    operation, group, artifact, version, origins, timeout.Token,
                    operation == JavaDependencyOperation.Install ?
                        (installScopeSelector.SelectedItem as string ?? "compile") :
                        operation == JavaDependencyOperation.Update ?
                            (candidateScope ?? "compile") :
                            operation == JavaDependencyOperation.ChangeScope ?
                            (installedScopeSelector.SelectedItem as string ?? "compile") : "compile"))
                {
                    if (!string.Equals(initialProject, projectFile, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Il progetto selezionato è cambiato.");
                    string message = change.Summary + "\n\nProgetto: " +
                        System.IO.Path.GetFileName(initialProject) +
                        "\nJAR risolti: " + change.JarCount +
                        "\n\nMaven risolve le dipendenze runtime anche transitive. " +
                        "I file JAR precedenti resteranno sul disco come copia di recupero " +
                        "ma saranno rimossi dal classpath.\n" +
                        "Il progetto verrà salvato con un backup. Confermare?";
                    if (MessageBox.Show(message, "Gestione pacchetti Plain",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    {
                        JavaJpmProjectLogger.Info(initialProject, "JPM", "Operazione annullata dall'utente: .javaproj invariato.");
                        status.Text = "Operazione annullata.";
                        return;
                    }
                    change.Apply();
                    JavaJpmProjectLogger.Info(initialProject, "JPM", change.Summary +
                        ". Backup: " + change.BackupPath);
                    CancelAll();
                    updatesResults.ItemsSource = null;
                    RefreshInstalled();
                    JavaLanguageSupport.ProjectSystem.Dependencies.JavaDependencyLanguageSync.RefreshAll();
                    status.Text = "Operazione Plain completata. Dettagli in Output JPM.";
                    // Post-commit audit (Install/Update/Remove/ChangeScope), no modal dialogs.
                    QueuePlainConflictAudit("operazione " + operation);
                    plainConflictExplorer.Reload();
                }
            }
            catch (OperationCanceledException)
            {
                JavaJpmProjectLogger.Warning(initialProject, "Maven/Plain",
                    "Risoluzione interrotta per annullamento o timeout.");
                status.Text = "Operazione Plain interrotta. Dettagli in Output JPM.";
            }
            catch (Exception ex) when (ex is System.IO.IOException ||
                ex is System.IO.InvalidDataException || ex is UnauthorizedAccessException ||
                ex is ArgumentException || ex is NotSupportedException ||
                ex is InvalidOperationException || ex is XmlException)
            {
                JavaJpmProjectLogger.Error(initialProject, "JPM Plain", ex);
                var rejected = ex as JavaPlainVersionConflictGuard.CandidateConflictException;
                if (rejected != null)
                    plainConflictExplorer.ShowRejectedCandidate(initialProject,
                        rejected.Candidate, operation.ToString());
                // S2B: the S2A pre-commit guard may reject a conflicted candidate.
                // Do not claim the installed classpath itself contains this conflict.
                if (ex is InvalidOperationException && ex.Message.StartsWith(
                    "Conflitto tra versioni transitive Plain:", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref plainConflictAuditRevision);
                    plainConflictsIndicator.Visibility = Visibility.Visible;
                    plainConflictsIndicator.Text = "⚠ Operazione bloccata: conflitto nelle dipendenze proposte.";
                    plainConflictsIndicator.ToolTip = "Classpath invariato. Dettagli in Output JPM.";
                }
                status.Text = "Pacchetto Plain non modificato. Dettagli in Output JPM.";
            }
            finally
            {
                plainPackageBusy = false;
                RefreshDependencyActions();
            }
        }

        // JPM-S2A: explicit, non-mutating audit; keep heavy ZIP inspection off the UI thread.
        // S2B: same read-only audit for manual checks and automatic post-commit checks.
        // Results of a previous project/operation are discarded, including after project switch.
        private Task AuditPlainConflictsAsync()
        {
            QueuePlainConflictAudit("verifica manuale");
            return Task.CompletedTask;
        }

        private void QueuePlainConflictAudit(string reason)
        {
            int revision = Interlocked.Increment(ref plainConflictAuditRevision);
            if (installed == null || !string.Equals(installed.Backend, "Plain",
                StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(projectFile))
            {
                plainConflictsIndicator.Visibility = Visibility.Collapsed;
                return;
            }
            string selectedProject = projectFile;
            plainConflictsIndicator.Visibility = Visibility.Visible;
            plainConflictsIndicator.Text = "Conflitti Plain: verifica in corso...";
            plainConflictsIndicator.ToolTip = "Analisi dei JAR sul disco; il report sarà registrato in Output JPM.";
            _ = RunPlainConflictAuditAsync(selectedProject, revision, reason);
        }

        private async Task RunPlainConflictAuditAsync(string selectedProject,
            int revision, string reason)
        {
            // Large JAR archives may take time; do not block the UI thread.
            // Serialize scans and discard stale requests before opening ZIP files.
            await plainConflictAuditGate.WaitAsync();
            try
            {
                if (revision != Volatile.Read(ref plainConflictAuditRevision) ||
                    !string.Equals(selectedProject, projectFile,
                        StringComparison.OrdinalIgnoreCase)) return;
                auditingPlainConflicts = true;
                RefreshDependencyActions();
                JavaJpmProjectLogger.Info(selectedProject, "JPM conflitti",
                    "Avvio analisi " + reason + " (sola lettura).");
                var report = await Task.Run(() =>
                    JavaPlainVersionConflictGuard.Audit(selectedProject));
                if (revision != Volatile.Read(ref plainConflictAuditRevision) ||
                    !string.Equals(selectedProject, projectFile,
                        StringComparison.OrdinalIgnoreCase)) return;

                string summary = "Conflitti Plain: " + report.ConflictCount +
                    "; JAR non verificabili: " + report.UnverifiedCount + ".";
                plainConflictsIndicator.Text = report.ConflictCount > 0 ?
                    "⚠ " + summary : report.UnverifiedCount > 0 ?
                    "⚠ " + summary : "✓ " + summary;
                plainConflictsIndicator.ToolTip =
                    "Verifica " + reason + " completata. Dettagli in Output JPM.";
                // Keep the full report out of the tool window. Do not block edits
                // or mediate versions silently: S2A still guards candidate commits.
                if (report.ConflictCount > 0 || report.UnverifiedCount > 0)
                    JavaJpmProjectLogger.Warning(selectedProject, "JPM conflitti",
                        summary + "\n" + report.Details);
                else
                    JavaJpmProjectLogger.Info(selectedProject, "JPM conflitti",
                        summary + "\n" + report.Details);
            }
            catch (Exception ex)
            {
                if (revision == Volatile.Read(ref plainConflictAuditRevision) &&
                    string.Equals(selectedProject, projectFile,
                        StringComparison.OrdinalIgnoreCase))
                {
                    plainConflictsIndicator.Text = "⚠ Conflitti Plain: verifica non riuscita.";
                    plainConflictsIndicator.ToolTip = "Errore nel report. Aprire Output JPM.";
                    try { JavaJpmProjectLogger.Error(selectedProject, "JPM conflitti", ex); }
                    catch (Exception) { /* Optional logging must not crash Visual Studio. */ }
                }
            }
            finally
            {
                auditingPlainConflicts = false;
                RefreshDependencyActions();
                plainConflictAuditGate.Release();
            }
        }

        // JPM-LOG-20261124: explicit, read-only Maven/Gradle dependency diagnostic.
        private async Task RunJpmToolLogAsync()
        {
            if (jpmToolBusy || installed == null || string.IsNullOrWhiteSpace(projectFile)) return;
            string selectedProject = projectFile;
            string selectedBackend = installed.Backend;
            if (selectedBackend != "Maven" && selectedBackend != "Gradle") return;
            jpmToolBusy = true;
            RefreshDependencyActions();
            JavaJpmProjectLogger.Open(selectedProject);
            status.Text = "Diagnostica dipendenze avviata. Consultare Output JPM.";
            try
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
                {
                    int result = await JavaJpmToolDiagnostics.RunAsync(selectedProject,
                        selectedBackend, timeout.Token);
                    if (string.Equals(selectedProject, projectFile, StringComparison.OrdinalIgnoreCase))
                        status.Text = result == 0 ? "Diagnostica completata. Dettagli in Output JPM." :
                            "Diagnostica non riuscita. Dettagli in Output JPM.";
                }
            }
            catch (OperationCanceledException)
            {
                JavaJpmProjectLogger.Warning(selectedProject, selectedBackend,
                    "Diagnostica interrotta o scaduta dopo cinque minuti.");
                if (string.Equals(selectedProject, projectFile, StringComparison.OrdinalIgnoreCase))
                    status.Text = "Diagnostica interrotta. Dettagli in Output JPM.";
            }
            catch (Exception ex) when (ex is System.IO.IOException ||
                ex is UnauthorizedAccessException || ex is ArgumentException ||
                ex is InvalidOperationException || ex is NotSupportedException ||
                ex is System.ComponentModel.Win32Exception)
            {
                JavaJpmProjectLogger.Error(selectedProject, selectedBackend, ex);
                if (string.Equals(selectedProject, projectFile, StringComparison.OrdinalIgnoreCase))
                    status.Text = "Diagnostica non riuscita. Dettagli in Output JPM.";
            }
            finally { jpmToolBusy = false; RefreshDependencyActions(); }
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
                    " dipendenze dirette · " + (installed.Backend == "Plain" ?
                        "JAR Plain: gestione assistita" : "modifica assistita");
                updatesNote.Text = "Il confronto è indicativo e non verifica i vincoli del progetto. " +
                    "Selezionare un risultato e confermare prima di modificare i file.";
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is XmlException ||
                ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                installed = null;
                installedResults.ItemsSource = null;
                projectInfo.Text = "Impossibile leggere le dipendenze locali.";
                JavaJpmProjectLogger.Error(projectFile, "JPM installati", ex);
                installedNote.Text = "Impossibile leggere le dipendenze. Dettagli in Output JPM.";
            }
            RefreshDependencyActions();
        }

        private void ResetSourceSelector()
        {
            ResetSuggestionPolicy();
            suppressSourceSelection = true;
            try
            {
                selectedSourceUrl = null;
                sourceSelector.ItemsSource = null;
                sourceSelector.IsEnabled = false;
                sourceSelector.ToolTip = "Rilevare prima le origini del progetto.";
            }
            finally { suppressSourceSelection = false; }
        }

        private void RefreshSourceSelector()
        {
            // The list contains usable endpoints only; Gradle PREFER_PROJECT and
            // non-HTTPS entries remain readable in the discovery details.
            var candidates = packageSources == null
                ? new List<JavaPackageSource>()
                : packageSources.Sources
                    .Where(JavaPackageMultiRepositorySearchService.IsEligible)
                    .GroupBy(x => x.Url.AbsoluteUri.TrimEnd('/'),
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.OrderByDescending(x => x.HasServerConfiguration).First())
                    .OrderBy(x => x.Name ?? x.Id ?? "", StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Url.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            var choices = new List<JavaPackageSourceChoice>
            {
                new JavaPackageSourceChoice(null,
                    "Tutte le origini attive (" + candidates.Count + ")")
            };
            choices.AddRange(candidates.Select(x => new JavaPackageSourceChoice(x,
                (IsCentralRepository(x) ? "Maven Central" :
                    (x.Name ?? x.Id ?? "Repository")) + " · " +
                    x.Url.Authority + x.Url.AbsolutePath.TrimEnd('/'))));
            var previouslySelected = selectedSourceUrl;
            var chosen = choices.FirstOrDefault(x => x.Source != null &&
                string.Equals(x.Source.Url.AbsoluteUri.TrimEnd('/'), previouslySelected,
                    StringComparison.OrdinalIgnoreCase)) ?? choices[0];
            suppressSourceSelection = true;
            try
            {
                sourceSelector.ItemsSource = choices;
                sourceSelector.SelectedItem = chosen;
                sourceSelector.IsEnabled = candidates.Count > 0;
                sourceSelector.ToolTip = "Origine per ricerca, versioni e aggiornamenti. " +
                    "I repository non attivi e non HTTPS sono esclusi. Dettagli nel tooltip di Rileva origini.";
                selectedSourceUrl = chosen.Source == null ? null :
                    chosen.Source.Url.AbsoluteUri.TrimEnd('/');
            }
            finally { suppressSourceSelection = false; }
            ResetSuggestionPolicy();
        }

        private IList<JavaPackageSource> SelectedSources()
        {
            if (packageSources == null) return new List<JavaPackageSource>();
            if (string.IsNullOrWhiteSpace(selectedSourceUrl)) return packageSources.Sources;
            // Selecting one URL must not accidentally include another repository.
            return packageSources.Sources
                .Where(x => JavaPackageMultiRepositorySearchService.IsEligible(x) &&
                    string.Equals(x.Url.AbsoluteUri.TrimEnd('/'), selectedSourceUrl,
                        StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.HasServerConfiguration)
                .Take(1).ToList();
        }

        private void ResetSourceBoundResults()
        {
            ResetSuggestionPolicy();
            ++searchGeneration; ++versionsGeneration; ++updatesGeneration;
            Cancel(ref searchCts); Cancel(ref versionsCts); Cancel(ref updatesCts);
            lastPage = null;
            lastCatalog = null;
            currentPage = 1;
            browseResults.ItemsSource = null;
            pageInfo.Text = "";
            previousButton.IsEnabled = false;
            nextButton.IsEnabled = false;
            ResetVersionsPaging();
            versionPicker.ItemsSource = null;
            versionPicker.IsHitTestVisible = false;
            versionInfo.Text = "";
            versionOriginInfo.Text = "";
            details.Text = "Seleziona una libreria per vedere i dettagli.";
            retryVersionsButton.Visibility = Visibility.Collapsed;
            ShowVersionPlaceholder("Seleziona una libreria per visualizzare le versioni.");
            updatesResults.ItemsSource = null;
            updatesNote.Text = "Premi Controlla versioni per verificare l'origine selezionata.";
            updateButton.IsEnabled = installed != null;
            SetSearchBusy(false);
        }

        // F4F2103-COORDINATE-DISCOVERY: this popup does not consume docked layout space.
        private void ConfigureSuggestions(Grid toolbar)
        {
            DecorateList(suggestionList, "Display");
            var row = new FrameworkElementFactory(typeof(StackPanel));
            row.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("Display"));
            var name = new FrameworkElementFactory(typeof(TextBlock));
            name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("ArtifactName"));
            name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            name.SetValue(TextBlock.FontSizeProperty, 13.0);
            name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            name.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            row.AppendChild(name);
            var coordinate = new FrameworkElementFactory(typeof(TextBlock));
            coordinate.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Coordinate"));
            coordinate.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            coordinate.SetValue(TextBlock.OpacityProperty, 0.90);
            coordinate.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            row.AppendChild(coordinate);
            var origin = new FrameworkElementFactory(typeof(TextBlock));
            origin.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Origin"));
            origin.SetValue(TextBlock.FontSizeProperty, 11.0);
            origin.SetValue(TextBlock.OpacityProperty, 0.74);
            origin.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            row.AppendChild(origin);
            suggestionList.ItemTemplate = new DataTemplate { VisualTree = row };
            suggestionList.Padding = new Thickness(0);
            suggestionList.Focusable = false;
            suggestionList.MaxHeight = 325;
            ScrollViewer.SetHorizontalScrollBarVisibility(suggestionList, ScrollBarVisibility.Disabled);
            suggestionList.ItemContainerStyle.Setters.Add(new Setter(Control.FocusableProperty, false));
            suggestionHint.Margin = new Thickness(7, 5, 7, 4);
            var panel = new DockPanel();
            ThemeBackground(panel);
            DockPanel.SetDock(suggestionHint, Dock.Top);
            panel.Children.Add(suggestionHint);
            panel.Children.Add(suggestionList);
            var border = new Border { BorderThickness = new Thickness(1), Child = panel };
            border.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            border.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            border.SetBinding(FrameworkElement.WidthProperty, new System.Windows.Data.Binding("ActualWidth")
                { Source = toolbar });
            suggestionPopup.PlacementTarget = toolbar;
            suggestionPopup.Placement = PlacementMode.Bottom;
            suggestionPopup.StaysOpen = true;
            suggestionPopup.Child = border;
            toolbar.Children.Add(suggestionPopup);
            var watermark = CaptionText("Cerca per nome o coordinate");
            watermark.IsHitTestVisible = false;
            watermark.Opacity = 0.65;
            watermark.Margin = new Thickness(9, 0, 15, 0);
            watermark.VerticalAlignment = VerticalAlignment.Center;
            watermark.TextWrapping = TextWrapping.NoWrap;
            watermark.TextTrimming = TextTrimming.CharacterEllipsis;
            watermark.Visibility = searchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            toolbar.Children.Add(watermark);

            // Explicit consent is necessary for custom/mixed origins. No credentials,
            // project paths or installed dependency data are sent to the public index.
            ThemeControl(publicSuggestions);
            ThemePublicSuggestionsCheckbox(publicSuggestions);
            publicSuggestions.Content = "Attiva suggerimenti da Maven Central";
            publicSuggestions.ToolTip = "Invia il testo digitato all'indice pubblico di Maven Central. " +
                "La ricerca finale verifica solo le origini selezionate; i suggerimenti non garantiscono disponibilità.";
            publicSuggestions.Visibility = Visibility.Collapsed;
            publicSuggestions.Margin = new Thickness(0, 3, 0, 0);
            toolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            toolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(publicSuggestions, 1);
            Grid.SetColumnSpan(publicSuggestions, 3);
            toolbar.Children.Add(publicSuggestions);
            publicSuggestions.Checked += (s, e) =>
            {
                publicSuggestions.Content = "Disattiva suggerimenti da Maven Central";
                if (resettingSuggestions) return;
                searchBox.Focus();
                _ = LoadSuggestionsAsync();
            };
            publicSuggestions.Unchecked += (s, e) =>
            {
                publicSuggestions.Content = "Attiva suggerimenti da Maven Central";
                StopSuggestions();
            };
            searchBox.TextChanged += (s, e) =>
            {
                watermark.Visibility = searchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (!acceptingSuggestion && searchBox.IsKeyboardFocused) _ = LoadSuggestionsAsync();
                else StopSuggestions();
            };
            searchBox.PreviewKeyDown += SuggestionKeyDown;
            searchBox.LostKeyboardFocus += (s, e) => StopSuggestions();
            searchBox.SizeChanged += (s, e) =>
            {
                if (suggestionPopup.IsOpen) StopSuggestions();
            };
            suggestionList.PreviewMouseLeftButtonDown += (s, e) =>
            {
                var element = e.OriginalSource as DependencyObject;
                var item = element == null ? null :
                    ItemsControl.ContainerFromElement(suggestionList, element) as ListBoxItem;
                var choice = item == null ? null : item.Content as JavaCoordinateSuggestion;
                if (choice == null) return;
                e.Handled = true;
                AcceptSuggestion(choice, true);
            };
        }

        private bool CentralOnlySuggestions()
        {
            var sources = SelectedSources().Where(JavaPackageMultiRepositorySearchService.IsEligible).ToList();
            return sources.Count > 0 && sources.All(IsCentralRepository);
        }

        private bool CanUsePublicSuggestions()
        {
            return CentralOnlySuggestions() || (publicSuggestions.Visibility == Visibility.Visible &&
                publicSuggestions.IsChecked == true);
        }

        private void ResetSuggestionPolicy()
        {
            StopSuggestions();
            resettingSuggestions = true;
            try
            {
                publicSuggestions.IsChecked = false;
                publicSuggestions.Visibility = packageSources != null &&
                    SelectedSources().Any(JavaPackageMultiRepositorySearchService.IsEligible) &&
                    !CentralOnlySuggestions() ? Visibility.Visible : Visibility.Collapsed;
            }
            finally { resettingSuggestions = false; }
        }

        private void StopSuggestions()
        {
            ++suggestionGeneration;
            Cancel(ref suggestionCts);
            suggestionPopup.IsOpen = false;
            suggestionList.ItemsSource = null;
        }

        // F4F21034-RELEVANCE-RANKING: match user intent before index order.
        // Never infer that a result is official/secure from its coordinates.
        private static int SuggestionScore(string coordinate, string term)
        {
            if (string.IsNullOrWhiteSpace(coordinate) || string.IsNullOrWhiteSpace(term))
                return -1;
            int sep = coordinate.IndexOf(':');
            if (sep < 1 || sep == coordinate.Length - 1) return -1;
            string group = coordinate.Substring(0, sep).ToLowerInvariant();
            string artifact = coordinate.Substring(sep + 1).ToLowerInvariant();
            string query = term.Trim().ToLowerInvariant();
            int querySep = query.IndexOf(':');
            if (querySep >= 0)
            {
                if (querySep == 0 || query.IndexOf(':', querySep + 1) >= 0) return -1;
                string qGroup = query.Substring(0, querySep);
                string qArtifact = query.Substring(querySep + 1);
                if (!group.StartsWith(qGroup, StringComparison.Ordinal) ||
                    !artifact.StartsWith(qArtifact, StringComparison.Ordinal)) return -1;
                return 50000 + (group == qGroup ? 5000 : 0) +
                    (artifact == qArtifact ? 5000 : 0);
            }
            string normalized = query.Replace(' ', '-').Replace('_', '-');
            if (artifact == normalized) return 45000;
            if (artifact.StartsWith(normalized, StringComparison.Ordinal)) return 40000;
            if (artifact.Contains(normalized)) return 35000;
            if (group == query) return 33000;
            if (group.StartsWith(query, StringComparison.Ordinal)) return 30000;
            // Words may be in different order: "data jpa" matches "spring-data-jpa".
            string[] words = System.Text.RegularExpressions.Regex.Split(query,
                @"[^\p{L}\p{Nd}]+")
                .Where(x => x.Length > 0).ToArray();
            if (words.Length == 0) return -1;
            bool allInArtifact = words.All(w => artifact.Contains(w));
            if (allInArtifact) return 27000 + words.Count(w => artifact.StartsWith(w,
                StringComparison.Ordinal)) * 400;
            if (words.All(w => artifact.Contains(w) || group.Contains(w)))
                return 19000 + words.Count(w => artifact.Contains(w)) * 600;
            if (group.Contains(query)) return 12000;
            return -1; // Description-only matches are not actionable as coordinates.
        }

        private static int SuggestionOriginBoost(JavaCoordinateSuggestion item)
        {
            if (item.Origin.StartsWith("Nel progetto", StringComparison.Ordinal)) return 900;
            if (item.Origin.StartsWith("Ricerca recente", StringComparison.Ordinal)) return 450;
            return 0;
        }

        private static List<JavaCoordinateSuggestion> RankSuggestions(
            IEnumerable<JavaCoordinateSuggestion> candidates, string term)
        {
            return candidates.Where(x => x != null && SuggestionScore(x.Coordinate, term) >= 0)
                .GroupBy(x => x.Coordinate, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(SuggestionOriginBoost).First())
                .OrderByDescending(x => SuggestionScore(x.Coordinate, term) + SuggestionOriginBoost(x))
                .ThenBy(x => x.ArtifactName.Length)
                .ThenBy(x => x.Coordinate, StringComparer.OrdinalIgnoreCase)
                .Take(12).ToList();
        }

        private List<JavaCoordinateSuggestion> LocalSuggestions(string term)
        {
            var rows = new List<JavaCoordinateSuggestion>();
            if (installed != null)
                foreach (var dep in installed.Items.Where(x => x.MavenCoordinate))
                    rows.Add(new JavaCoordinateSuggestion(dep.GroupId + ":" + dep.ArtifactId,
                        "Nel progetto (non verifica l'origine)"));
            rows.AddRange(recentCoordinates.Select(x => new JavaCoordinateSuggestion(x, "Ricerca recente")));
            return RankSuggestions(rows, term);
        }

        private bool CurrentSuggestions(int generation, CancellationToken token, string term)
        {
            return generation == suggestionGeneration && !token.IsCancellationRequested &&
                IsLoaded && tabs.SelectedIndex == 0 && searchBox.IsKeyboardFocused &&
                string.Equals(searchBox.Text.Trim(), term, StringComparison.Ordinal);
        }

        private void ShowSuggestions(List<JavaCoordinateSuggestion> rows, string hint)
        {
            var previous = suggestionList.SelectedItem as JavaCoordinateSuggestion;
            suggestionHint.Text = hint;
            suggestionList.ItemsSource = rows;
            suggestionList.SelectedItem = previous == null ? null :
                rows.FirstOrDefault(x => x.Coordinate == previous.Coordinate);
            suggestionPopup.IsOpen = true;
        }

        private async Task LoadSuggestionsAsync()
        {
            StopSuggestions();
            string term = searchBox.Text.Trim();
            if (!IsLoaded || tabs.SelectedIndex != 0 || !searchBox.IsKeyboardFocused ||
                string.IsNullOrWhiteSpace(projectFile) || term.Length < 3 || term.Length > 160 ||
                term.Any(char.IsControl)) return;
            int generation = suggestionGeneration;
            var local = LocalSuggestions(term);
            if (local.Count > 0)
                ShowSuggestions(local, "Suggerimenti locali · ↑ ↓ Invio / Tab");
            if (!CanUsePublicSuggestions())
            {
                if (local.Count == 0 && publicSuggestions.Visibility == Visibility.Visible)
                    ShowSuggestions(local, "Nessun suggerimento locale. Per cercare nomi pubblici, attiva Suggerimenti da Maven Central.");
                return;
            }
            ReplaceToken(ref suggestionCts);
            var token = suggestionCts.Token;
            try
            {
                // No network per keystroke: cancel old requests and wait for typing to stop.
                await Task.Delay(450, token);
                if (!CurrentSuggestions(generation, token, term) || !CanUsePublicSuggestions()) return;
                var result = await service.SuggestAsync(term, token);
                if (!CurrentSuggestions(generation, token, term) || !CanUsePublicSuggestions()) return;
                var coordinates = RankSuggestions(local.Concat(result.Items.Select(x =>
                    new JavaCoordinateSuggestion(x.Coordinate, "Maven Central · disponibilità da verificare"))), term);
                ShowSuggestions(coordinates, coordinates.Count == 0 ? "Nessun suggerimento trovato." :
                    "Coordinate · ↑ ↓ Invio per cercare, Tab per completare");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is JavaMavenCentralSearchException ||
                ex is ArgumentException || ex is System.IO.IOException)
            {
                if (CurrentSuggestions(generation, token, term))
                    ShowSuggestions(local, "Suggerimenti pubblici non disponibili. Puoi usare Cerca.");
            }
            finally
            {
                if (generation == suggestionGeneration) Cancel(ref suggestionCts);
            }
        }

        private void SuggestionKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (suggestionPopup.IsOpen || suggestionCts != null)
                { StopSuggestions(); e.Handled = true; }
                return;
            }
            if (!suggestionPopup.IsOpen || suggestionList.Items.Count == 0) return;
            if (e.Key == Key.Down || e.Key == Key.Up)
            {
                int index = suggestionList.SelectedIndex;
                if (index < 0) index = e.Key == Key.Down ? 0 : suggestionList.Items.Count - 1;
                else index = Math.Max(0, Math.Min(suggestionList.Items.Count - 1,
                    index + (e.Key == Key.Down ? 1 : -1)));
                suggestionList.SelectedIndex = index;
                suggestionList.ScrollIntoView(suggestionList.SelectedItem);
                e.Handled = true;
            }
            else if ((e.Key == Key.Enter || e.Key == Key.Tab) &&
                suggestionList.SelectedItem is JavaCoordinateSuggestion)
            {
                var row = (JavaCoordinateSuggestion)suggestionList.SelectedItem;
                e.Handled = true;
                AcceptSuggestion(row, e.Key == Key.Enter);
            }
        }

        private void AcceptSuggestion(JavaCoordinateSuggestion row, bool searchNow)
        {
            StopSuggestions();
            acceptingSuggestion = true;
            try
            {
                searchBox.Text = row.Coordinate;
                searchBox.CaretIndex = searchBox.Text.Length;
                recentCoordinates.Remove(row.Coordinate);
                recentCoordinates.Insert(0, row.Coordinate);
                if (recentCoordinates.Count > 30) recentCoordinates.RemoveAt(30);
                searchBox.Focus();
            }
            finally { acceptingSuggestion = false; }
            if (searchNow) _ = StartSearchAsync(1);
        }


        private async Task StartSearchAsync(int page)
        {
            StopSuggestions();
            if (string.IsNullOrWhiteSpace(projectFile)) return;
            if (packageSources == null)
            { status.Text = "Attendere il rilevamento delle origini (oppure premere Rileva origini)."; return; }
            string search = searchBox.Text.Trim();
            if (search.Length == 0) { status.Text = "Inserire un termine di ricerca."; return; }
            if (!string.Equals(query, search, StringComparison.Ordinal)) page = 1;
            query = search;
            int generation = ++searchGeneration;
            ReplaceToken(ref searchCts);
            ReplaceToken(ref versionsCts);
            ++versionsGeneration;
            lastCatalog = null;
            versionOriginInfo.Text = "";
            ResetVersionsPaging();
            versionPicker.ItemsSource = null;
            versionPicker.IsHitTestVisible = false;
            retryVersionsButton.Visibility = Visibility.Collapsed;
            ShowVersionPlaceholder("Seleziona una libreria per visualizzare le versioni.");
            details.Text = "Seleziona una libreria per vedere i dettagli.";
            versionInfo.Text = "";
            var token = searchCts.Token;
            var selectedSources = SelectedSources();
            SetSearchBusy(true);
            JavaJpmProjectLogger.Info(projectFile, "JPM ricerca", "Ricerca: " + search +
                "; pagina " + page + "; repository selezionati: " + selectedSources.Count + ".");
            status.Text = "Ricerca nelle origini attive...";
            try
            {
                JavaCentralPage<JavaCentralArtifact> found;
                string g, a;
                string note;
                if (JavaPackageMultiRepositorySearchService.TryCoordinate(search, out g, out a))
                {
                    var catalog = await repositoryService.GetCatalogAsync(g, a,
                        selectedSources, token);
                    if (generation != searchGeneration || token.IsCancellationRequested) return;
                    var artifacts = new List<JavaCentralArtifact>();
                    if (catalog.Versions.Count != 0)
                        artifacts.Add(new JavaCentralArtifact(g, a, catalog.Versions[0], "jar",
                            catalog.Versions.Count, null, new string[0]));
                    found = new JavaCentralPage<JavaCentralArtifact>(artifacts, artifacts.Count, 1, 20);
                    note = "Coordinate cercate in " + catalog.RepositoriesQueried + " origini. " +
                        catalog.WarningText;
                }
                else
                {
                    // Ordinary Maven repositories have no portable full-text search API.
                    if (!selectedSources.Any(IsCentralRepository))
                    {
                        status.Text = "Ricerca testuale non disponibile: Maven Central non è un'origine attiva. " +
                            "Usare groupId:artifactId o attivare Suggerimenti da Maven Central per trovare le coordinate.";
                        browseResults.ItemsSource = null;
                        pageInfo.Text = "";
                        return;
                    }
                    found = await service.SearchAsync(search, page, 20, token);
                    note = "Ricerca testuale su Maven Central. Per le altre origini usare groupId:artifactId.";
                }
                if (generation != searchGeneration || token.IsCancellationRequested) return;
                lastPage = found;
                currentPage = page;
                browseResults.ItemsSource = found.Items.ToList();
                if (found.Items.Count > 0) browseResults.SelectedIndex = 0;
                pageInfo.Text = "Pagina " + page + " · " + found.TotalCount + " risultati";
                previousButton.IsEnabled = currentPage > 1;
                nextButton.IsEnabled = found.HasNextPage;
                status.Text = (found.Items.Count == 0 ? "Nessuna libreria trovata. " :
                    "Ricerca completata. ") + note;
            }
            catch (OperationCanceledException)
            { if (generation == searchGeneration) status.Text = "Ricerca annullata."; }
            catch (Exception ex) when (ex is JavaMavenCentralSearchException ||
                ex is ArgumentException || ex is ArgumentOutOfRangeException ||
                ex is System.IO.IOException)
            {
                JavaJpmProjectLogger.Error(projectFile, "JPM ricerca", ex);
                if (generation == searchGeneration)
                    status.Text = "Ricerca non riuscita. Dettagli in Output JPM.";
            }
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
            requestedVersionsPage = page;
            lastVersionsPage = null;
            lastCatalog = null;
            versionOriginInfo.Text = "";
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
            if (packageSources == null)
            { ShowVersionPlaceholder("Attendere il rilevamento delle origini."); return; }
            ShowVersionPlaceholder("Caricamento versioni dai repository attivi...");
            details.Text = artifact.Coordinate + "\nUltima versione catalogata: " + artifact.LatestVersion +
                "\nVersioni disponibili: " + artifact.VersionCount +
                "\nOrigini: " + SelectedSources().Count(JavaPackageMultiRepositorySearchService.IsEligible) + " attive";
            versionInfo.Text = "Caricamento pagina " + page + "...";
            var token = versionsCts.Token;
            var selectedSources = SelectedSources();
            try
            {
                if (debounceSelection) await Task.Delay(300, token);
                var catalog = await repositoryService.GetCatalogAsync(artifact.GroupId,
                    artifact.ArtifactId, selectedSources, token);
                if (generation != versionsGeneration || token.IsCancellationRequested) return;
                lastCatalog = catalog;
                int offset = checked((page - 1) * VersionPageSize);
                var items = catalog.Versions.Skip(offset).Take(VersionPageSize)
                    .Select(v => new JavaCentralVersion(artifact.GroupId, artifact.ArtifactId,
                        v, "jar", null, new string[0])).ToList();
                var versions = new JavaCentralPage<JavaCentralVersion>(items, catalog.Versions.Count,
                    page, VersionPageSize);
                lastVersionsPage = versions;
                var rows = versions.Items.Select(v =>
                {
                    string originNames;
                    catalog.VersionOrigins.TryGetValue(v.Version, out originNames);
                    return new JavaPackageVersionDisplay(v, originNames);
                }).ToList();
                versionPicker.ItemsSource = rows;
                versionPicker.IsHitTestVisible = rows.Count > 0;
                ShowVersionPlaceholder(versions.Items.Count == 0
                    ? "Nessuna versione disponibile in questa pagina." : null);
                if (versions.Items.Count > 0) versionPicker.SelectedIndex = 0;
                long totalPages = Math.Max(1L,
                    (versions.TotalCount + VersionPageSize - 1L) / VersionPageSize);
                versionsPageInfo.Text = "Pagina " + page + " / " + totalPages;
                previousVersionsButton.IsEnabled = page > 1;
                nextVersionsButton.IsEnabled = versions.HasNextPage;
                long first = versions.Items.Count == 0 ? 0 : (long)(page - 1) * VersionPageSize + 1;
                long last = versions.Items.Count == 0 ? 0 : first + versions.Items.Count - 1;
                versionInfo.Text = "Versioni " + first + "–" + last + " di " + versions.TotalCount +
                    " · Origini consultate: " + catalog.RepositoriesQueried;
                if (catalog.Warnings.Count > 0)
                    JavaJpmProjectLogger.Warning(projectFile, "JPM versioni", catalog.WarningText);
            }
            catch (OperationCanceledException)
            {
                if (generation == versionsGeneration)
                { ShowVersionPlaceholder("Caricamento annullato."); versionInfo.Text = ""; versionsPageInfo.Text = ""; }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is ArgumentOutOfRangeException ||
                ex is System.IO.IOException)
            {
                if (generation == versionsGeneration)
                {
                    ShowVersionPlaceholder("Versioni temporaneamente non disponibili.");
                    JavaJpmProjectLogger.Error(projectFile, "JPM versioni", ex);
                    versionInfo.Text = "Errore caricamento. Dettagli in Output JPM.";
                    versionsPageInfo.Text = "Pagina " + page + " · errore";
                    retryVersionsButton.Visibility = Visibility.Visible;
                }
            }
        }

        private async Task LoadUpdatesAsync()
        {
            RefreshInstalled();
            if (installed == null) return;
            if (packageSources == null)
            { updatesNote.Text = "Attendere il rilevamento delle origini del progetto."; return; }
            var items = installed.Items.Where(x => x.MavenCoordinate &&
                !x.Version.StartsWith("(", StringComparison.Ordinal)).Take(25).ToList();
            if (items.Count == 0)
            {
                updatesNote.Text = "Nessuna dipendenza con coordinate letterali da verificare.";
                updatesResults.ItemsSource = null;
                return;
            }
            int generation = ++updatesGeneration;
            ReplaceToken(ref updatesCts);
            var token = updatesCts.Token;
            var selectedSources = SelectedSources();
            updateButton.IsEnabled = false;
            updatesNote.Text = "Controllo nei repository attivi (max 25 dipendenze dirette)...";
            try
            {
                var candidates = new List<JavaPackageUpdateCandidate>();
                var failures = 0;
                foreach (var dep in items)
                {
                    token.ThrowIfCancellationRequested();
                    JavaRepositoryCatalogResult catalog;
                    try { catalog = await repositoryService.GetCatalogAsync(dep.GroupId,
                        dep.ArtifactId, selectedSources, token); }
                    catch (Exception ex) when (ex is ArgumentException || ex is System.IO.IOException)
                    {
                        JavaJpmProjectLogger.Error(projectFile, "JPM aggiornamenti", ex);
                        failures++; continue;
                    }
                    if (catalog.Warnings.Count > 0) failures++;
                    string latest = JavaPackageUpdateSelector.NewestCompatible(dep.Version,
                        catalog.Versions);
                    if (!string.IsNullOrWhiteSpace(latest))
                        candidates.Add(new JavaPackageUpdateCandidate(dep, latest));
                }
                if (generation != updatesGeneration || token.IsCancellationRequested) return;
                updatesResults.ItemsSource = candidates;
                updatesNote.Text = candidates.Count + " aggiornamenti stabili individuati. " +
                    (failures > 0 ? failures + " verifiche con avvisi/errori. " : "") +
                    "Catalogo aggregato dai repository attivi; confronto indicativo, " +
                    "nessuna modifica senza conferma. Vincoli Gradle/Maven non valutati.";
                RefreshDependencyActions();
            }
            catch (OperationCanceledException)
            { if (generation == updatesGeneration) updatesNote.Text = "Verifica annullata."; }
            finally { if (generation == updatesGeneration) updateButton.IsEnabled = true; }
        }

        private async Task DiscoverSourcesAsync()
        {
            if (string.IsNullOrWhiteSpace(projectFile) || installed == null) return;
            var path = projectFile;
            var backend = Convert.ToString(installed.Backend);
            int generation = ++sourcesGeneration;
            ReplaceToken(ref sourcesCts);
            var token = sourcesCts.Token;
            packageSources = null;
            ResetSuggestionPolicy();
            sourceSelector.IsEnabled = false;
            discoverSourcesButton.ToolTip = "Rilevamento in corso...";
            // Source refresh invalidates results based on old origins.
            ++searchGeneration; ++versionsGeneration; ++updatesGeneration;
            Cancel(ref searchCts); Cancel(ref versionsCts); Cancel(ref updatesCts);
            lastCatalog = null;
            lastPage = null;
            browseResults.ItemsSource = null;
            versionPicker.ItemsSource = null;
            versionOriginInfo.Text = "";
            updatesResults.ItemsSource = null;
            SetSearchBusy(false);
            discoverSourcesButton.Content = "Annulla rilevamento";
            try
            {
                var snapshot = await Task.Run(() => JavaPackageSourceDiscovery.Discover(path, backend, token), token);
                if (generation != sourcesGeneration || token.IsCancellationRequested) return;
                packageSources = snapshot;
                JavaJpmProjectLogger.Info(path, "JPM repository", snapshot.Describe());
                RefreshSourceSelector();
                // No duplicate origin list in the header. Retain diagnostics,
                // including excluded/inactive repositories, via the button tooltip.
                discoverSourcesButton.ToolTip = snapshot.Describe();
                status.Text = "Origini rilevate: " + snapshot.Sources.Count +
                    (snapshot.Warnings.Count > 0 ?
                        " (" + snapshot.Warnings.Count + " avvisi: passa sul pulsante Rileva origini)." : ".") +
                    " Ricerca per coordinate sui repository selezionati.";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                JavaJpmProjectLogger.Error(path, "JPM repository", ex);
                if (generation != sourcesGeneration) return;
                ResetSourceSelector();
                discoverSourcesButton.ToolTip =
                    "Configurazione non disponibile. Verificare settings.gradle, build.gradle, pom.xml o settings.xml.";
                status.Text = "Origini non rilevate. Posiziona il mouse su Rileva origini per i dettagli.";
            }
            finally
            {
                if (generation == sourcesGeneration)
                {
                    Cancel(ref sourcesCts);
                    discoverSourcesButton.Content = "Rileva origini";
                }
            }
        }

        private void StopSourceDiscovery()
        {
            ++sourcesGeneration;
            if (sourcesCts != null)
            {
                Cancel(ref sourcesCts);
                discoverSourcesButton.ToolTip = "Rilevamento annullato: premi Rileva origini per riprovare.";
            }
            discoverSourcesButton.Content = "Rileva origini";
        }

        private void CancelAll()
        {
            StopSuggestions();
            StopSourceDiscovery();
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
        private static bool IsCentralRepository(JavaPackageSource source)
        {
            if (!JavaPackageMultiRepositorySearchService.IsEligible(source)) return false;
            return (source.Url.Host.Equals("repo.maven.apache.org", StringComparison.OrdinalIgnoreCase) ||
                source.Url.Host.Equals("repo1.maven.org", StringComparison.OrdinalIgnoreCase)) &&
                source.Url.AbsolutePath.TrimEnd('/').Equals("/maven2", StringComparison.OrdinalIgnoreCase);
        }

        private void SetSearchBusy(bool busy)
        {
            searchButton.IsEnabled = !busy && !string.IsNullOrWhiteSpace(projectFile);
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

        private static void ApplyResponsiveBrowseLayout(Grid host, ListBox results,
            FrameworkElement detailsPanel, GridSplitter splitter, bool stacked)
        {
            if (stacked)
            {
                host.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
                host.ColumnDefinitions[1].Width = new GridLength(0);
                host.ColumnDefinitions[2].Width = new GridLength(0);
                host.ColumnDefinitions[0].MinWidth = 0;
                host.ColumnDefinitions[2].MinWidth = 0;
                host.RowDefinitions[0].Height = new GridLength(2, GridUnitType.Star);
                host.RowDefinitions[0].MinHeight = 65;
                host.RowDefinitions[1].Height = new GridLength(6);
                host.RowDefinitions[2].Height = new GridLength(3, GridUnitType.Star);
                host.RowDefinitions[2].MinHeight = 135;
                Grid.SetColumn(results, 0); Grid.SetRow(results, 0);
                Grid.SetColumn(detailsPanel, 0); Grid.SetRow(detailsPanel, 2);
                detailsPanel.Margin = new Thickness(0, 8, 0, 0);
                Grid.SetColumn(splitter, 0); Grid.SetRow(splitter, 1);
                splitter.ResizeDirection = GridResizeDirection.Rows;
                splitter.ResizeBehavior = GridResizeBehavior.PreviousAndNext;
                splitter.Width = double.NaN; splitter.Height = 6;
            }
            else
            {
                host.RowDefinitions[0].MinHeight = 0;
                host.RowDefinitions[2].MinHeight = 0;
                host.ColumnDefinitions[0].Width = new GridLength(1.8, GridUnitType.Star);
                host.ColumnDefinitions[0].MinWidth = 90;
                host.ColumnDefinitions[1].Width = new GridLength(6);
                host.ColumnDefinitions[2].Width = new GridLength(3.2, GridUnitType.Star);
                host.ColumnDefinitions[2].MinWidth = 175;
                host.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
                host.RowDefinitions[1].Height = new GridLength(0);
                host.RowDefinitions[2].Height = new GridLength(0);
                Grid.SetColumn(results, 0); Grid.SetRow(results, 0);
                Grid.SetColumn(detailsPanel, 2); Grid.SetRow(detailsPanel, 0);
                detailsPanel.Margin = new Thickness(12, 0, 0, 0);
                Grid.SetColumn(splitter, 1); Grid.SetRow(splitter, 0);
                splitter.ResizeDirection = GridResizeDirection.Columns;
                splitter.ResizeBehavior = GridResizeBehavior.PreviousAndNext;
                splitter.Width = 6; splitter.Height = double.NaN;
            }
        }

        private static DataTemplate VersionRowTemplate()
        {
            var row = new FrameworkElementFactory(typeof(StackPanel));
            row.SetValue(StackPanel.OrientationProperty, Orientation.Vertical);
            row.SetBinding(FrameworkElement.ToolTipProperty,
                new System.Windows.Data.Binding("FullDescription"));
            var version = new FrameworkElementFactory(typeof(TextBlock));
            version.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Version"));
            version.SetResourceReference(TextBlock.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            version.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            version.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            row.AppendChild(version);
            var origins = new FrameworkElementFactory(typeof(TextBlock));
            origins.SetBinding(TextBlock.TextProperty,
                new System.Windows.Data.Binding("OriginSummary"));
            origins.SetResourceReference(TextBlock.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            origins.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            origins.SetValue(TextBlock.FontSizeProperty, 11.0);
            origins.SetValue(TextBlock.OpacityProperty, 0.80);
            origins.SetValue(TextBlock.MarginProperty, new Thickness(0, 2, 0, 0));
            row.AppendChild(origins);
            return new DataTemplate { VisualTree = row };
        }

        internal static DataTemplate ListItemTemplate(string property)
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

        // F4F2841-PROJECT-SELECTOR-THEME: self-contained dark/light VS ComboBox.
        // Stock WPF ComboBox uses OS colors for its selection area and popup;
        // explicit VS resources keep both the selected value and entries readable.
        // JPM-S2C1-RESPONSIVE-COMBO-20261124: share the existing native-looking VS ComboBox template.
        internal static void ThemeProjectSelector(ComboBox selector)
        {
            ThemeControl(selector);
            // DisplayMemberPath and ItemTemplate cannot be set together.
            selector.ClearValue(ItemsControl.DisplayMemberPathProperty);
            selector.ItemTemplate = ListItemTemplate("DisplayName");
            selector.MinHeight = 29;
            selector.MaxDropDownHeight = 350;
            selector.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            // Clicking the non-focusable toggle still leaves the ComboBox ready
            // for keyboard navigation once the list is opened.
            selector.DropDownOpened += (sender, args) =>
            {
                if (!selector.IsKeyboardFocusWithin) selector.Focus();
            };

            var itemRoot = new FrameworkElementFactory(typeof(Border));
            itemRoot.Name = "JavaProjectOptionChrome";
            itemRoot.SetResourceReference(Border.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            itemRoot.SetValue(Border.PaddingProperty, new Thickness(9, 6, 9, 6));
            var itemContent = new FrameworkElementFactory(typeof(ContentPresenter));
            itemContent.SetValue(ContentPresenter.ContentSourceProperty, "Content");
            itemRoot.AppendChild(itemContent);
            var optionTemplate = new ControlTemplate(typeof(ComboBoxItem)) { VisualTree = itemRoot };
            var optionHover = new Trigger { Property = ComboBoxItem.IsMouseOverProperty, Value = true };
            optionHover.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush(Color.FromArgb(42, 0, 122, 204)), "JavaProjectOptionChrome"));
            optionTemplate.Triggers.Add(optionHover);
            var optionSelected = new Trigger { Property = ComboBoxItem.IsSelectedProperty, Value = true };
            optionSelected.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush(Color.FromArgb(54, 0, 122, 204)), "JavaProjectOptionChrome"));
            optionSelected.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
            optionTemplate.Triggers.Add(optionSelected);
            var optionStyle = new Style(typeof(ComboBoxItem));
            optionStyle.Setters.Add(new Setter(Control.TemplateProperty, optionTemplate));
            optionStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,
                HorizontalAlignment.Stretch));
            selector.ItemContainerStyle = optionStyle;

            // An explicit template prevents the native OS ComboBox popup from
            // becoming light while Visual Studio uses the dark theme.
            var shell = new FrameworkElementFactory(typeof(Grid));
            var chrome = new FrameworkElementFactory(typeof(Border));
            chrome.Name = "JavaProjectSelectorChrome";
            chrome.SetResourceReference(Border.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            chrome.SetResourceReference(Border.BorderBrushProperty,
                EnvironmentColors.ToolWindowBorderBrushKey);
            chrome.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
            chrome.SetValue(FrameworkElement.MinHeightProperty, 27.0);
            var face = new FrameworkElementFactory(typeof(Grid));

            var selectedText = new FrameworkElementFactory(typeof(ContentPresenter));
            selectedText.SetBinding(ContentPresenter.ContentProperty,
                new System.Windows.Data.Binding("SelectionBoxItem")
                { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            selectedText.SetBinding(ContentPresenter.ContentTemplateProperty,
                new System.Windows.Data.Binding("SelectionBoxItemTemplate")
                { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            selectedText.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 3, 28, 3));
            selectedText.SetValue(FrameworkElement.VerticalAlignmentProperty,
                VerticalAlignment.Center);
            selectedText.SetValue(UIElement.IsHitTestVisibleProperty, false);
            face.AppendChild(selectedText);

            var arrow = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
            arrow.SetValue(System.Windows.Shapes.Path.DataProperty,
                Geometry.Parse("M 0 0 L 4 4 L 8 0 Z"));
            arrow.SetResourceReference(System.Windows.Shapes.Path.FillProperty,
                EnvironmentColors.ToolWindowTextBrushKey);
            arrow.SetValue(FrameworkElement.WidthProperty, 9.0);
            arrow.SetValue(FrameworkElement.HeightProperty, 5.0);
            arrow.SetValue(FrameworkElement.HorizontalAlignmentProperty,
                HorizontalAlignment.Right);
            arrow.SetValue(FrameworkElement.VerticalAlignmentProperty,
                VerticalAlignment.Center);
            arrow.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 10, 0));
            arrow.SetValue(UIElement.IsHitTestVisibleProperty, false);
            face.AppendChild(arrow);

            var hitTarget = new FrameworkElementFactory(typeof(ToggleButton));
            hitTarget.SetValue(Control.FocusableProperty, false);
            hitTarget.SetBinding(ToggleButton.IsCheckedProperty,
                new System.Windows.Data.Binding("IsDropDownOpen") {
                    Mode = System.Windows.Data.BindingMode.TwoWay,
                    RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var hitFace = new FrameworkElementFactory(typeof(Border));
            hitFace.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            hitTarget.SetValue(Control.TemplateProperty,
                new ControlTemplate(typeof(ToggleButton)) { VisualTree = hitFace });
            face.AppendChild(hitTarget);
            chrome.AppendChild(face);
            shell.AppendChild(chrome);

            var popup = new FrameworkElementFactory(typeof(Popup));
            popup.Name = "PART_Popup";
            popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
            popup.SetValue(Popup.AllowsTransparencyProperty, true);
            popup.SetValue(Popup.StaysOpenProperty, false);
            popup.SetBinding(Popup.IsOpenProperty,
                new System.Windows.Data.Binding("IsDropDownOpen") {
                    Mode = System.Windows.Data.BindingMode.TwoWay,
                    RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            popup.SetBinding(FrameworkElement.WidthProperty,
                new System.Windows.Data.Binding("ActualWidth")
                { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var popupChrome = new FrameworkElementFactory(typeof(Border));
            popupChrome.SetResourceReference(Border.BackgroundProperty,
                EnvironmentColors.ToolWindowBackgroundBrushKey);
            popupChrome.SetResourceReference(Border.BorderBrushProperty,
                EnvironmentColors.ToolWindowBorderBrushKey);
            popupChrome.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
            scroll.SetValue(ScrollViewer.CanContentScrollProperty, true);
            scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty,
                ScrollBarVisibility.Auto);
            scroll.SetValue(FrameworkElement.MaxHeightProperty, 350.0);
            scroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
            popupChrome.AppendChild(scroll);
            popup.AppendChild(popupChrome);
            shell.AppendChild(popup);

            var projectTemplate = new ControlTemplate(typeof(ComboBox)) { VisualTree = shell };
            var focused = new Trigger { Property = ComboBox.IsKeyboardFocusWithinProperty, Value = true };
            focused.Setters.Add(new Setter(Border.BorderBrushProperty,
                new SolidColorBrush(Color.FromRgb(0, 122, 204)), "JavaProjectSelectorChrome"));
            projectTemplate.Triggers.Add(focused);
            var disabled = new Trigger { Property = ComboBox.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.65));
            projectTemplate.Triggers.Add(disabled);
            selector.Template = projectTemplate;
        }

        // F4F21033-COMPACT-CONSENT-ACTION: a quiet, theme-aware text action.
        // Keep native CheckBox semantics (IsChecked, Checked/Unchecked, Space and
        // screen readers), without a custom glyph or box drawn into the toolbar.
        private static void ThemePublicSuggestionsCheckbox(CheckBox checkbox)
        {
            var label = new FrameworkElementFactory(typeof(TextBlock));
            label.Name = "JavaSuggestionsConsentText";
            label.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Content")
            {
                RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent
            });
            label.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 2, 2, 2));
            label.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            label.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
            label.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            label.SetResourceReference(TextBlock.ForegroundProperty,
                EnvironmentColors.ToolWindowTextBrushKey);

            var template = new ControlTemplate(typeof(CheckBox)) { VisualTree = label };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(TextBlock.TextDecorationsProperty,
                TextDecorations.Underline, "JavaSuggestionsConsentText"));
            template.Triggers.Add(hover);
            var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            focus.Setters.Add(new Setter(TextBlock.TextDecorationsProperty,
                TextDecorations.Underline, "JavaSuggestionsConsentText"));
            template.Triggers.Add(focus);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.55));
            template.Triggers.Add(disabled);

            checkbox.Template = template;
            checkbox.IsThreeState = false;
            checkbox.Cursor = Cursors.Hand;
            checkbox.HorizontalAlignment = HorizontalAlignment.Left;
            checkbox.MinHeight = 20;
            System.Windows.Automation.AutomationProperties.SetName(checkbox,
                "Consenti suggerimenti pubblici da Maven Central");
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
