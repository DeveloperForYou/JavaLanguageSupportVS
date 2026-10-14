# Gradle editor acceptance project

Open this **folder** in Visual Studio 2026. Wait for JDT LS to import Gradle.
Run the tests in the sibling `../EditorFeaturesSmoke/README.md`.

```powershell
gradle build
gradle run
```

The Gradle wrapper is deliberately not included. Use installed Gradle 8.5+
with JDK 21+, or generate a wrapper locally (`gradle wrapper`).
