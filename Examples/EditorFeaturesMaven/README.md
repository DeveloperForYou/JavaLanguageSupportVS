# Maven editor acceptance project

Open this **folder** in Visual Studio 2026 (not a single .java file), wait until JDT LS
finishes importing Maven, then run the tests described in the sibling
`../EditorFeaturesSmoke/README.md`.

Command-line acceptance:

```powershell
mvn -B -DskipTests package
mvn -B exec:java -Dexec.mainClass=featurecheck.FeatureMain
```

The first Maven run may require access to download the plugins. JDK 21+ required.
