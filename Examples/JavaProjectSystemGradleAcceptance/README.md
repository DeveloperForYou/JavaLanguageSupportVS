# Java Project System – Gradle acceptance

Copia indipendente di `Examples/JavaProjectSystemAcceptance/` (Maven). I sorgenti Java,
le risorse e i package provengono dalla copia **locale** del progetto di riferimento
al momento dell'applicazione. Il progetto Maven originale non viene modificato.

## Apertura e test

Aprire `HelloJavaGradle.javaproj` in Visual Studio 2026 Experimental. La proprietà
`JavaBuildSystem` è `Gradle`. Provare **Compila**, **Pulisci** e **Ricompila**.

Da Developer PowerShell for Visual Studio 2026:

```powershell
msbuild .\HelloJavaGradle.javaproj /t:Build /v:normal
msbuild .\HelloJavaGradle.javaproj /t:Clean /v:normal
msbuild .\HelloJavaGradle.javaproj /t:Rebuild /v:normal
```

Prima verificare Gradle:

```powershell
gradle.bat --version
```

Il backend usa `gradlew.bat` se presente nel progetto, altrimenti un'installazione
Gradle raggiungibile dal runner F3.A. Non viene distribuito un wrapper Gradle
senza la sua configurazione/JAR; dopo avere installato Gradle si può generare
un wrapper con `gradle wrapper` e versionarlo insieme al progetto.

## Configurazione Gradle

- Plugin: `java`.
- `options.release = 21` come nel `pom.xml` originale; serve un JDK che supporti
  la release Java 21 e una versione Gradle compatibile con quel JDK.
- Build: `gradle build`; Clean: `gradle clean`; Rebuild: `clean` + `build`.
- Output: `build/` (nascosto in Esplora soluzioni dal CPS).
- Dipendenze esterne e plugin Maven **non** vengono migrati automaticamente:
  il POM di esempio usa solo il compilatore Java e nessuna dependency esterna.
- I test JUnit richiedono dipendenze e configurazioni Gradle esplicite.

Limitazioni generali: `docs/IMPLEMENTATION_LIMITS.md` nella radice del repository.
