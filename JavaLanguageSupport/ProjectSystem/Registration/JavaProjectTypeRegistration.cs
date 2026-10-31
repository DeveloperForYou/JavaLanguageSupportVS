using System.ComponentModel.Composition;
using JavaLanguageSupport.Commands;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.VS;

namespace JavaLanguageSupport.ProjectSystem.Registration
{
    /// <summary>
    /// Pack F1: CPS project type registration in the EXISTING VSIX assembly.
    /// The .javaproj is Visual Studio metadata. pom.xml/build.gradle remains
    /// the independent build definition used by other Java IDEs.
    /// </summary>
    [Export]
    [AppliesTo(UniqueCapability)]
    [ProjectTypeRegistration(
    ProjectTypeGuid,
    "Java Project",
    "Java Project Files (*.javaproj);*.javaproj",
    ProjectExtension,
    "Java",
    resourcePackageGuid: JavaCommandsPackage.PackageGuid,
    PossibleProjectExtensions = ProjectExtension)]
    internal sealed class JavaProjectTypeRegistration
    {
        // Keep this capability in sync with <ProjectCapability> in .javaproj.
        internal const string UniqueCapability = "JavaLanguageSupportCps";
        internal const string ProjectExtension = "javaproj";
        // Permanent identifier: do not change it in subsequent packs.
        internal const string ProjectTypeGuid = "286cdcbd-76df-41ea-b49b-6407943cd24b";
    }
}
