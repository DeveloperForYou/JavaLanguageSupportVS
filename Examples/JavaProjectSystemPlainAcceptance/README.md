# Java Project System – Plain acceptance

Copia indipendente di `Examples/JavaProjectSystemAcceptance/` (Maven). I sorgenti Java,
le risorse e i package provengono dalla copia **locale** del progetto di riferimento
al momento dell'applicazione; il progetto Maven originale non viene modificato.

## Apertura e test

Aprire `HelloJavaPlain.javaproj` in Visual Studio 2026 Experimental. In Esplora
soluzioni, verificare sorgenti, directory e icone. Provare **Compila**, **Pulisci**
e **Ricompila** dal menu contestuale del progetto.

Da Developer PowerShell for Visual Studio 2026:

```powershell
msbuild .\HelloJavaPlain.javaproj /t:Build /v:normal
msbuild .\HelloJavaPlain.javaproj /t:Clean /v:normal
msbuild .\HelloJavaPlain.javaproj /t:Rebuild /v:normal
```

Requisiti: un JDK con `javac.exe` sul PATH o configurato in `JAVA_HOME`.
Output: `.java-vs/classes/`; *Clean* non elimina altre directory dell'utente.

## Limiti del backend Plain

- I sorgenti compilati sono gli item `JavaCompile` del CPS: nessun `pom.xml`,
  nessuna risoluzione delle dipendenze Maven o Gradle.
- Il backend Plain F3.A usa `javac` senza passargli automaticamente `--release 21`,
  anche se il POM originale impostava la release 21. Il JDK installato determina
  il livello di compilazione, salvo ulteriori impostazioni del backend.
- Le risorse `src/main/resources` vengono copiate, ma la compilazione Plain
  non le include automaticamente nella directory `classes`.
- Il file `Main.java` del progetto sorgente non contiene necessariamente un metodo
  `main`: questo esempio serve a collaudare **Build/Clean/Rebuild**, non Run/F5.
- Eventuali test che richiedono JUnit o altre librerie esterne non sono gestiti
  automaticamente dal backend Plain.

Limitazioni generali: `docs/IMPLEMENTATION_LIMITS.md` nella radice del repository.
