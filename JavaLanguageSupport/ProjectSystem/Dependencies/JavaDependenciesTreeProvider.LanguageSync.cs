using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.Properties;
using JavaLanguageSupport.ProjectSystem.Packages;

namespace JavaLanguageSupport.ProjectSystem.Dependencies
{
    internal partial class JavaDependenciesTreeProvider
    {
        private readonly JavaDependencyLanguageSync dependencyLanguageSync;
        private volatile IProjectVersionedValue<IProjectCatalogSnapshot> dependencyCatalog;

        private void ScheduleDependencyLanguageSync(string backend,
            IProjectVersionedValue<IProjectCatalogSnapshot> change)
        {
            if (string.IsNullOrWhiteSpace(backend)) return;
            try
            {
                var instance = change.Value.Project.ProjectInstance;
                string file = Path.GetFullPath(instance.FullPath);
                if (!File.Exists(file)) return;
                string root = Path.GetDirectoryName(file);
                string[] jars = string.Equals(backend, "Plain", StringComparison.OrdinalIgnoreCase)
                    ? instance.GetItems("JavaJarReference").Select(item =>
                    {
                        string path = item.GetMetadataValue("FullPath");
                        if (string.IsNullOrWhiteSpace(path)) path = Path.Combine(root, item.EvaluatedInclude);
                        return Path.GetFullPath(path);
                    }).Where(path => path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                      .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray()
                    : new string[0];
                // A Plain project can build with javac while JDT LS diagnoses
                // packages incorrectly if it does not know the Java source roots.
                // Mirror the VS Java package resolver, not the .javaproj root.
                string[] sourcePaths = new string[0];
                if (string.Equals(backend, "Plain", StringComparison.OrdinalIgnoreCase))
                {
                    var discovered = JavaSourceRootResolver.Discover(root, "Plain")
                        .Where(x => x.Exists).ToList();
                    // Prioritize src/main/java and src/test/java. Never send
                    // both src and a nested root: Eclipse disallows overlap.
                    var nested = discovered.Where(x =>
                        !string.Equals(x.RelativePath, "src", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    var selected = nested.Count > 0 ? nested : discovered;
                    sourcePaths = selected.Select(x => x.RelativePath.Replace('\\', '/'))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                }
                dependencyCatalog = change;
                dependencyLanguageSync.Update(file, backend, instance.GetPropertyValue("JavaJdkHome"), jars, sourcePaths);
            }
            catch (Exception) { /* CPS can be reevaluating or unloading. */ }
        }

        private void ForceDependencyTreeRefresh()
        {
            lock (mavenSync) mavenFingerprint = null;
            lock (gradleSync) gradleFingerprint = null;
            lock (plainJarSync) plainJarFingerprint = null;
            lock (jdkSync) jdkFingerprint = null;
            RequestMavenSnapshot();
            RequestGradleSnapshot();
            RequestPlainJarSnapshot();
            var change = dependencyCatalog;
            if (change != null)
                ScheduleJdkRefresh(change.Value.Project.ProjectInstance.GetPropertyValue("JavaBuildSystem"), change);
        }
    }
}
