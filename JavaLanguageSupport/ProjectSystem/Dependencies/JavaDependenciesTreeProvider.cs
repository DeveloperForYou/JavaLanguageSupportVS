using System;
using System.ComponentModel.Composition;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.Properties;

namespace JavaLanguageSupport.ProjectSystem.Dependencies
{
    /// <summary>
    /// First CPS dependency-tree layer: a read-only, virtual subtree.
    /// The Maven/Gradle/JDK entries are category placeholders, NOT resolved dependencies.
    /// A root-graft provider is deliberately used instead of enabling the .NET
    /// DependenciesTree capability (which belongs to a different dependency model).
    /// </summary>
    [Export(ExportContractNames.ProjectTreeProviders.PhysicalViewRootGraft,
        typeof(IProjectTreeProvider))]
    [AppliesTo("JavaLanguageSupportCps")]
    internal class JavaDependenciesTreeProvider : ProjectTreeProviderBase
    {
        private readonly IActiveConfiguredProjectSubscriptionService subscriptionService;
        private IDisposable subscriptionLink;

        [ImportingConstructor]
        public JavaDependenciesTreeProvider(
            IProjectThreadingService threadingService,
            UnconfiguredProject unconfiguredProject,
            IActiveConfiguredProjectSubscriptionService subscriptionService)
            : base(threadingService, unconfiguredProject)
        {
            this.subscriptionService = subscriptionService;
        }

        protected override void Initialize()
        {
            base.Initialize();

            // Publish the root immediately, even if evaluation has not arrived yet.
            // Do not invent the backend while waiting for the MSBuild snapshot.
            _ = SubmitTreeUpdateAsync((snapshot, exports, cancellationToken) =>
                Task.FromResult(new TreeUpdateResult(CreateRoot(null))));

            var target = new ActionBlock<IProjectVersionedValue<IProjectCatalogSnapshot>>(
                OnProjectChangedAsync);
            subscriptionLink = subscriptionService.ProjectCatalogSource.SourceBlock.LinkTo(
                target, new DataflowLinkOptions { PropagateCompletion = true });
        }

        private Task OnProjectChangedAsync(
            IProjectVersionedValue<IProjectCatalogSnapshot> change)
        {
            string backend = null;
            try
            {
                if (change != null && change.Value != null && change.Value.Project != null &&
                    change.Value.Project.ProjectInstance != null)
                {
                    backend = change.Value.Project.ProjectInstance.GetPropertyValue(
                        "JavaBuildSystem");
                }
            }
            catch (Exception)
            {
                // Project evaluation can be unavailable during unload or reload.
                // Keep the hierarchy intact and do not break project loading.
            }

            // Evaluate only the authoritative .javaproj property. Never run Maven,
            // Gradle, javac, or JDT LS while generating the tree.
            return SubmitTreeUpdateAsync((snapshot, exports, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new TreeUpdateResult(CreateRoot(backend)));
            });
        }

        private IProjectTree CreateRoot(string backend)
        {
            var referenceIcon = KnownMonikers.Reference.ToProjectSystemType();
            var libraryIcon = KnownMonikers.Library.ToProjectSystemType();
            var rootFlags = ProjectTreeFlags.Create(
                ProjectTreeFlags.Common.BubbleUp | ProjectTreeFlags.Common.VirtualFolder);
            var childFlags = ProjectTreeFlags.Create(ProjectTreeFlags.Common.VirtualFolder);

            IProjectTree root = NewTree("Dipendenze", icon: referenceIcon,
                expandedIcon: referenceIcon, flags: rootFlags);

            // Only a category: actual JDK home/version is F4.B+ scope.
            root = root.Add(NewTree("JDK", icon: libraryIcon,
                expandedIcon: libraryIcon, flags: childFlags)).Parent;

            string category = null;
            if (string.Equals(backend, "Maven", StringComparison.OrdinalIgnoreCase))
                category = "Maven";
            else if (string.Equals(backend, "Gradle", StringComparison.OrdinalIgnoreCase))
                category = "Gradle";
            else if (string.Equals(backend, "Plain", StringComparison.OrdinalIgnoreCase))
                category = "JAR Esterni";

            if (category != null)
                root = root.Add(NewTree(category, icon: referenceIcon,
                    expandedIcon: referenceIcon, flags: childFlags)).Parent;

            return root;
        }

        protected override ConfiguredProjectExports GetActiveConfiguredProjectExports(
            ConfiguredProject newActiveConfiguredProject)
        {
            return GetActiveConfiguredProjectExports<JavaDependenciesConfiguredProjectExports>(
                newActiveConfiguredProject);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (subscriptionLink != null)
                {
                    subscriptionLink.Dispose();
                    subscriptionLink = null;
                }
            }
            base.Dispose(disposing);
        }

        [Export]
        protected class JavaDependenciesConfiguredProjectExports : ConfiguredProjectExports
        {
            [ImportingConstructor]
            public JavaDependenciesConfiguredProjectExports(ConfiguredProject configuredProject)
                : base(configuredProject)
            {
            }
        }
    }
}
