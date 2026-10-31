# Java Project System — Pack F1 (CPS prototype)

**No new C# project is added to `JavaLanguageSupportVS.sln`.** The CPS
registration belongs to the already existing `JavaLanguageSupport.csproj`, in
`ProjectSystem/Registration/JavaProjectTypeRegistration.cs`.

## Scope

F1 registers `.javaproj` via `ProjectTypeRegistration`/MEF, with the
`JavaLanguageSupportCps` project capability. The independent Maven acceptance
project is under `Examples/JavaProjectSystemAcceptance/`. No existing language
server, editor, rename, navigation or color component is rewritten.

`HelloJavaF1.javaproj` is **optional IDE metadata**, not an authoritative Java
build configuration. Other IDEs open `pom.xml` directly. JDK requirements,
dependencies, sources and build tasks belong in Maven/Gradle, *never in a
Visual Studio-specific XML setting as the only copy*. The example Maven project
builds with `mvn -B package` on JDK 21+ and can also be imported into IntelliJ,
Eclipse or NetBeans without `.javaproj`.

## Compilation dependencies for VSIX

- `Microsoft.VisualStudio.ProjectSystem.SDK` **17.9.380** (NuGet; latest public
  stable SDK package confirmed for this initial prototype). Restore NuGet.
- Existing `Microsoft.VisualStudio.SDK` and `Microsoft.VSSDK.BuildTools` remain.
- VS 2026 compatibility of this older CPS SDK **requires an Experimental test**.
  Package restore + C# compilation alone cannot establish runtime compatibility.

## Manual acceptance (VS 2026 Experimental)

1. Close Experimental, build the existing `JavaLanguageSupport` VSIX with the
   new NuGet package restored. Do not add any new C# projects to the solution.
2. Launch F5 Experimental. Choose **File > Open > Project/Solution** and open
   `Examples/JavaProjectSystemAcceptance/HelloJavaF1.javaproj` (or add it only
   to a separate throwaway acceptance solution, never to the VSIX solution).
3. Check it loads as a project in Solution Explorer. Open `src/main/java/demo/Main.java`
   and confirm existing Java editor, coloring and Navigation Bar still work.
4. Check CPS type GUID registration in the Experimental registry if Visual
   Studio reports *Unsupported project type*. The GUID is
   `{286cdcbd-76df-41ea-b49b-6407943cd24b}`. Capture ActivityLog.xml.
5. Check portability in a terminal: in the **acceptance project directory**,
   run `mvn -B package`. Open `pom.xml` in IntelliJ or Eclipse; the `.javaproj`
   file is not required.
6. F1 **does not implement native VS Build, project property pages, custom Java
   item types, templates, dependencies tree, F5 Java debugging or complete JDT LS
   solution-project synchronization**. These come in F2-F8. Native VS Build is
   explicitly marked unsupported rather than reporting false success.

**Status:** implementation prepared, **not compiled or tested on VS 2026** in
this environment. Do not mark F1 validated before a successful Experimental run.
