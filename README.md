# Java Language Support per Visual Studio 2026 (v0.4, sorgenti)

Prototipo **VSIX per Visual Studio IDE 2026, NON VS Code**. Questo pacchetto amplia il
prototipo precedente con comandi di menu reali per compilazione, esecuzione e test.

## Correzione v0.4: VSIX creato ma menu Strumenti senza comandi

La versione 0.3 generava il VSIX, ma conteneva **due problemi nella registrazione**:

1. `Commands.vsct` era incluso in `VSCTCompile` senza `<ResourceName>Menus.ctmenu</ResourceName>`. Il compilatore dei comandi deve associare la tabella menu esattamente al nome utilizzato da `[ProvideMenuResource("Menus.ctmenu", 1)]` in `JavaCommandsPackage.cs`.
2. Il file di registrazione TextMate si chiamava `JavaLanguageSupport.pkgdef`, **lo stesso nome del file di registrazione del VSPackage che l'SDK può generare**. Questo è un rischio di conflitto fra i due asset. È stato rinominato in `JavaTextMate.pkgdef` e il percorso è stato aggiornato nel manifest.

La v0.4 modifica inoltre la versione del pacchetto a `0.4.0` per distinguere il VSIX aggiornato da quello precedente.

**Test su Windows / VS 2026 non eseguito**: le modifiche correggono errori di configurazione verificabili dal codice, ma la visibilità dei menu deve essere confermata sull'installazione reale.

### Come provare v0.4

1. Chiudere tutte le finestre **Experimental** di Visual Studio 2026.
2. Estrarre lo ZIP v0.4 in una cartella nuova; aprire `JavaLanguageSupportVS.sln`.
3. Selezionare `Debug | Any CPU` e **Compila > Ricompila soluzione**.
4. Individuare `JavaLanguageSupport.vsix` nella cartella `bin/Debug` (o negli output effettivi del progetto).
5. Impostare il progetto come avvio e premere **F5**. Verificare di trovarsi nella seconda istanza con suffisso `Exp`.
6. In **Estensioni > Gestisci estensioni > Installate** verificare che la versione `0.4.0` compaia e sia abilitata. Se non c'è, non è stata distribuita; in questo caso installare il `.vsix` nell'istanza desiderata tramite VSIXInstaller e riavviare Visual Studio, oppure diagnosticare il deploy automatico di F5.
7. Aprire **Strumenti** e cercare `Java: Compila progetto`, `Java: Esegui progetto`, `Java: Esegui test` (indipendenti da JDT LS).

Se le voci non ci sono ancora: nel VSIX (che è un archivio ZIP) controllare che siano inclusi `JavaLanguageSupport.dll`, `JavaTextMate.pkgdef` e il pkgdef generato dal progetto. **Il fatto che il file `.vsix` esista non dimostra che sia installato e registrato nell'istanza che si sta usando.** Avviare l'istanza sperimentale con `/RootSuffix Exp /Log`, aprire `ActivityLog.xml` da `%APPDATA%\Microsoft\VisualStudio\18.0*_Exp\`, e cercare `JavaLanguageSupport`, `pkgdef` o `menu`. Il percorso dipende dall'instance id; si può localizzare con:

```powershell
Get-ChildItem "$env:APPDATA\Microsoft\VisualStudio" -Recurse -Filter ActivityLog.xml -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName
```

**Non è obbligatorio spuntare `Distribuisci` nella Gestione configurazione**: in certi progetti SDK-style la casella è disabilitata; controllare invece se F5 distribuisce effettivamente il VSIX. L'inclusione del VSIX nell'output e la sua installazione sono operazioni distinte.

---

## Correzione v0.3: perché i comandi non apparivano

Nella v0.2 mancavano impostazioni necessarie a un progetto **SDK-style** per
creare e distribuire correttamente un'estensione VSIX in Visual Studio 2026.
Questa versione aggiunge:

- `VSSDKBuildToolsAutoSetup=true`, `VsixDeployOnDebug=true`,
  `ProjectCapability=CreateVsixContainer` nel `.csproj`;
- `Debug|Any CPU.Deploy.0` nella `.sln`, per distribuire nell'istanza sperimentale;
- `Microsoft.VSSDK.BuildTools` 18.5.38461, come nell'esempio Microsoft per
  progetti SDK-style in **Visual Studio 2026 18.5 o successivo**;
- versione pacchetto 0.3.0 per distinguere la distribuzione aggiornata.

### Prova di visibilità dei menu (prima di JDT LS)

1. In **Visual Studio 2026 18.5+** con workload **Sviluppo di estensioni per Visual Studio**,
   estrai lo ZIP in una nuova cartella e apri `JavaLanguageSupportVS.sln`.
2. In **Compila > Gestione configurazione** se la casella `Distribuisci`/`Deploy` è disabilitata, lasciala com'è. In un progetto SDK-style si deve verificare il deploy con F5, non la casella.
3. Compila in `Debug|Any CPU` con `Ctrl+Shift+B`; nella cartella
   `JavaLanguageSupport\bin\Debug` cerca il file `.vsix`.
4. Premi F5 sul progetto `JavaLanguageSupport` per avviare **Visual Studio
   Experimental Instance**. Non aprire a mano un'altra istanza normale.
5. Nell'istanza sperimentale verifica **Estensioni > Gestisci estensioni > Installate**:
   `Java Language Support for Visual Studio 2026 (prototype)` deve comparire.
6. Controlla **Strumenti > Java: Compila progetto / Esegui progetto / Esegui test**.
   Queste voci non richiedono JDT LS per apparire.

Se l'estensione compare come installata ma i comandi mancano, raccogli il log
`ActivityLog.xml` dall'istanza sperimentale (avviata con `/RootSuffix Exp /Log`)
e verifica se il VSPackage `JavaCommandsPackage` è stato registrato/caricato.
Senza Visual Studio su Windows non è possibile dichiarare che la compilazione
sia già stata verificata: nel caso di errori serve il testo completo degli errori.

---

## Importante: NON è ancora equivalente al supporto C#

| Funzione | Stato |
| --- | --- |
| File `.java` riconosciuti e grammar Java | Sorgente VSIX incluso; da collaudare su VS 2026 |
| LSP (IntelliSense, hover, diagnosi, navigazione) | Client JDT LS incluso; dipende da negoziazione e workspace |
| Comando `Strumenti > Java: Compila progetto` | Implementato nel sorgente VSIX |
| Comando `Strumenti > Java: Esegui progetto` | Implementato nel sorgente VSIX |
| Comando `Strumenti > Java: Esegui test` | Implementato nel sorgente VSIX |
| Maven (`pom.xml`) | Compila/testa tramite Maven, esegue via `exec:java` |
| Gradle (`build.gradle`, `.kts`) | `build` / `test` / `run` (richiede application plugin) |
| Singolo `.java` | `javac -d .java-vs/classes`, poi `java -cp` |
| Compila Java con F6 | **Non implementato** (project system VS) |
| F5 con breakpoint, variabili, stepping | **Non implementato** (debug adapter + VS debugger engine) |
| Nuovo progetto Java da finestra Visual Studio | **Non implementato** (wizard / project system VS) |
| Esplora soluzioni nativo `.javaproj` | **Non implementato** (project system VS) |
| Test Explorer JUnit integrato | **Non implementato** (test adapter VS) |

**La compatibilità runtime e la build VSIX NON sono state verificate:** questo ambiente
non dispone di Visual Studio/Windows né del Visual Studio SDK. Il progetto richiede
compilazione, eventuali correzioni di API SDK e test sul PC Windows dell'utente.

## Prerequisiti

1. Visual Studio 2026 con workload **Sviluppo di estensioni per Visual Studio**.
2. **JDK >= 21**, `java.exe` e `javac.exe`; configurare `JAVA_HOME`.
3. JDT LS estratto (cartelle `plugins`, `config_win`) e `JDTLS_HOME`.
4. Per Maven: `mvn.cmd` nel PATH **oppure** `mvnw.cmd` nel progetto.
5. Per Gradle: `gradle.bat` nel PATH **oppure** `gradlew.bat` nel progetto.
6. Pacchetti NuGet disponibili per il restore.

### Configurazione PowerShell

```powershell
[Environment]::SetEnvironmentVariable('JAVA_HOME', 'C:\Program Files\Eclipse Adoptium\jdk-21', 'User')
[Environment]::SetEnvironmentVariable('JDTLS_HOME', 'C:\Dev\jdt-language-server', 'User')
```

I percorsi qui sopra sono **esempi**: controllare dove sono installati sul PC.
Riavviare Visual Studio e verificare:

```powershell
& "$env:JAVA_HOME\bin\java.exe" -version
Test-Path "$env:JDTLS_HOME\plugins"
Test-Path "$env:JDTLS_HOME\config_win"
```

## Esecuzione

1. Aprire `JavaLanguageSupportVS.sln` in Visual Studio 2026.
2. Ripristinare i pacchetti NuGet, poi compilare `Ctrl+Shift+B`.
3. Avviare istanza sperimentale con `F5` **della soluzione VSIX**.
4. Nell'istanza sperimentale: `File > Apri > Cartella` e aprire
   `Examples/MavenHello`, `Examples/GradleHello` o `Examples/HelloJava`.
5. Aprire nell'editor la classe Java `App.java`/`HelloJava.java` (il comando usa
   il *file attivo*, non un selettore generico di main class).
6. Cercare in `Strumenti` i comandi:
   - `Java: Compila progetto`
   - `Java: Esegui progetto`
   - `Java: Esegui test`
7. I log e gli errori di build appaiono in **Output > Java Build / Run / Test**.

I file Maven richiedono il plugin `exec-maven-plugin` per `exec:java`; Maven può
scaricarlo alla prima esecuzione. I progetti Gradle devono applicare il plugin
`application` per disporre del task `run`. Il file singolo non fornisce automaticamente
classpath di librerie esterne. Per il sample Maven usare **JDK 21+ e Maven**;
per il sample Gradle installare Gradle o inserire il wrapper.

## Architettura

- `JavaLanguageClient.cs`: avvia JDT LS via stdin/stdout, dalla cartella `JDTLS_HOME`.
- `JavaContentType.cs`, `Grammars/`: associa `.java` e grammatica editor.
- `JavaCommandsPackage.cs`: VSPackage, tre comandi nella voce Strumenti;
  usa il documento attivo tramite DTE e scrive nella Output Window.
- `JavaWorkspace.cs`: individua Maven/Gradle risalendo le cartelle dal file attivo,
  o usa il `.java` singolo; ricava il nome classe dal package.
- `JavaTaskExecutor.cs`: esegue wrapper/tool esterni e scrive stdout/stderr
  senza bloccare il thread UI.

La directory temporanea `.java-vs/` per file singoli è ignorata da git.

## Per arrivare davvero al livello C#

In Visual Studio il LSP **non** offre automaticamente project system o debugger.
Prossimi moduli architetturali:

1. **Project system Java** basato su VSPS/MPF oppure un bridge a MSBuild con
   estensione di progetto gestita; Solution Explorer, riferimenti, F6, pulizia
   e configurazioni. Il progetto non può fingere un `.csproj`.
2. **Java debug**: Microsoft `java-debug` (protocollo DAP con JDT LS) + un host
   DAP o un engine compatibile con il debugger di Visual Studio (AD7).
   Solo allora F5/F9/stack/locals/step sono realmente disponibili.
3. **Project wizard** Java semplice / Maven / Gradle; completamento dei manifest
   del progetto, dipendenze e classpath.
4. **Test adapter** per JUnit/TestNG nel Test Explorer e codice di glue per
   diagnosi/error list e MSBuild.

**Non confondere F5 della soluzione VSIX** (che esegue il debug dell'estensione)
con F5 del codice Java (che, allo stato attuale, NON è integrato).

## Fonti

- Microsoft LSP VS: https://learn.microsoft.com/en-us/visualstudio/extensibility/adding-an-lsp-extension
- VSIX compatibilità 2026: https://learn.microsoft.com/en-us/visualstudio/extensibility/migration/extension-compatibility
- Project system: https://github.com/microsoft/VSProjectSystem
- java-debug DAP: https://github.com/microsoft/java-debug
- Command SDK: https://learn.microsoft.com/en-us/visualstudio/extensibility/creating-an-extension-with-a-menu-command
