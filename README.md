# Java Language Support for Visual Studio 2026

Estensione **VSIX sperimentale** per sviluppare **Java nell'IDE Visual Studio 2026** (non Visual Studio Code). Le funzionalità semantiche si basano su [Eclipse JDT Language Server](https://github.com/eclipse-jdtls/eclipse.jdt.ls) tramite **Language Server Protocol (LSP)**; compilazione, esecuzione e test utilizzano JDK, Maven o Gradle.

> **Stato del progetto (15 novembre 2026):** le fasi editor **A1–E3**, il Project System CPS **F1–F2G** e la rinomina semantica di package **F2H/U3.A** sono stati verificati manualmente in Visual Studio 2026 Experimental. Il lifecycle JDT LS è stato stabilizzato con **U2**, e l’infrastruttura condivisa **U3.C.16 / U4.A–U4.D** porta Undo/Redo nativo alle rinomine, alle Code Actions, a Organize Imports e ai refactoring che applicano WorkspaceEdit locali. **Rimangono limiti intenzionali e funzionali:** vedere il [registro tecnico dei limiti](docs/IMPLEMENTATION_LIMITS.md). La patch **F3.A Native Build** introduce Build/Rebuild/Clean tramite MSBuild (collaudo VS Experimental ancora da eseguire); debug F5 e JUnit/TestNG in Test Explorer rimangono futuri. I collaudi sono manuali e non equivalgono a compatibilità garantita per ogni workspace/JDT LS.

## Funzionalità

| Area | Disponibilità attuale |
| --- | --- |
| Apertura e riconoscimento dei file `.java` | Implementati |
| Sintassi Java, classi, metodi, variabili, keyword e parentesi | TextMate e classificatori dedicati, con temi chiaro/scuro e colori configurabili |
| IntelliSense, completamento, suggerimenti e import | Integrazione con JDT LS; **Pack A1 e A3 collaudati** |
| Quick Info, Javadoc, `@see`, `{@link}`, Signature Help | **Pack A2 collaudato**, inclusi link e scorrimento dei popup |
| Diagnostica e Quick Actions (`Ctrl + .`) | **Pack A3 collaudato**; le azioni disponibili dipendono da JDT LS |
| Definizioni, implementazioni, riferimenti, Ctrl+Click e Peek (`Alt + F12`) | **Pack B1–B2 collaudati**, inclusa navigazione ai sorgenti JDK quando disponibile |
| Gerarchia chiamate e gerarchia tipi | Implementate su richieste LSP; disponibilità dei risultati dipendente da JDT LS |
| Rinomina di simboli, classi e file `.java` | **C1 + U4.A/U4.B/U4.B.1 collaudati**: anteprima, Undo/Redo globale e replay nella sessione; riaggancio schede dopo rinomina |
| Modifiche LSP a più file (`WorkspaceEdit`) | **C2 + U4.C collaudati**: anteprima, salvataggio, Undo/Redo globale e replay detached sicuro |
| Undo/Redo nativo di Visual Studio 2026 | **U3.C.16 + U4.A–U4.D collaudati**: package CPS, simboli, Quick Fix, Code Actions, Organize Imports e refactoring basati su WorkspaceEdit; cronologia limitata alla sessione |
| Estrai metodo/variabile/costante, Inline variabile, Cambia firma | **C3 + U4.D collaudati**; la proposta dipende da JDT LS, Undo globale quando è applicato un WorkspaceEdit locale |
| Genera costruttori, getter/setter, Override/Implement | **Pack D1 collaudato**: comandi e finestre di generazione codice |
| Completamento di `()`, `[]`, `{}`, virgolette e Javadoc | **Pack D2 collaudato** |
| Formattazione documento / selezione e commenti Java | **Pack D3 collaudato**: comandi nativi Visual Studio Ctrl+K,D/F/C/U; organizza import resta nel menu Strumenti |
| CodeLens, Inlay Hints e document highlight | **Pack E1 (CodeLens), E2 (Inlay Hints) ed E3 (stabilità e indicizzazione) collaudati**; document highlight presente |
| Compilazione, esecuzione e test | Comandi **Strumenti → Java** per JDK, Maven e Gradle |
| Progetti Spring Boot | **Supporto dedicato pianificato**; i comandi Maven/Gradle esistenti possono essere usati con progetti Spring Boot compatibili, ma non includono ancora funzionalità specifiche del framework |
| Traduzione dell'interfaccia dell'estensione (localizzazione) | **Pianificata**: separazione delle stringhe dell'interfaccia e scelta della lingua, a partire da italiano e inglese |
| Project System Java (`.javaproj`) in Esplora soluzioni | **F1/F2 collaudati**: apertura CPS, file e package; **F3.A preparato**: Build/Rebuild/Clean MSBuild per Maven/Gradle/Plain (collaudo VS Experimental pendente) |
| Aggiungi → Nuovo elemento — sorgenti Java | **Pack F2G.1**: sette template nativi (classe, classe astratta, interfaccia, enum, record, annotation, file vuoto), con scelta di source root, package e nome file; collaudati manualmente |
| Nuovo package Java | **Pack F2E/F2F**: riconoscimento root Maven/Gradle/Plain, finestra WPF, validazione, package nidificati e registrazione delle cartelle vuote come `Folder` in `.javaproj` |
| Nuovo package durante la creazione di un elemento | **Pack F2G.2**: pulsante «Nuovo package...» nel selettore dei sette template; il package appena creato viene selezionato automaticamente; collaudato |
| Moduli Java e cartelle risorse | **Pack F2G.2**: `module-info.java` per JDK 9+ (anche `open module`), più `src/main/resources` e `src/test/resources`; collaudato |
| Refactoring dei package Java | **F2H + U3.A/U3.C + U4.B.1 collaudati**: rinomina semantica di package/sottopackage, aggiornamento dichiarazioni e riferimenti, CPS e Undo/Redo; non è un trasferimento arbitrario fra progetti |
| Icone in Esplora soluzioni | **Pack F2C/F2D**: icona del progetto verificata; moniker custom per sorgenti Java, Maven, Gradle e file di configurazione presenti nel repository; verifica grafica F2D ancora in corso |
| Debug Java integrato (F5/F9/F10/F11) | **Non implementato** |
| JUnit/TestNG in Test Explorer | **Non implementato** |
| Designer visuale Swing/AWT/JavaFX | **Pianificato**, non implementato |

### Fasi di stabilizzazione dell'editor

| Pack | Ambito | Stato |
| --- | --- | --- |
| **A1** | IntelliSense e completamento | ✅ Collaudato |
| **A2** | Quick Info, Javadoc e Signature Help | ✅ Collaudato |
| **A3** | Diagnostica, Quick Actions e import | ✅ Collaudato |
| **B1** | Navigazione e affidabilità delle richieste | ✅ Collaudato |
| **B2** | Riferimenti, sorgenti JDK e Peek | ✅ Collaudato |
| **C1** | Rinomina simboli e file Java | ✅ Collaudato |
| **C2** | WorkspaceEdit multi-file, anteprima e Undo/Redo | ✅ Collaudato |
| **C3** | Refactoring avanzati | ✅ Collaudato |
| **D1–D3** | Generazione, editing automatico e formattazione | ✅ Collaudati |
| **E1** | CodeLens, riferimenti e implementazioni | ✅ Collaudato (con hotfix) |
| **E2** | Inlay Hints di parametri e tipi `var` | ✅ Collaudato (con hotfix) |
| **E3** | Indicizzazione JDT LS, stabilità dei colori e prestazioni | ✅ Collaudato (con hotfix) |
| **F1** | Project System CPS (`.javaproj`), inclusione dei file e operazioni di Esplora soluzioni | ✅ Collaudato manualmente |
| **F2A** | Sette modelli Java nativi in «Aggiungi nuovo elemento» | ✅ Duplicati risolti e modelli visibili |
| **F2B** | Icone dei modelli di elemento Java | 🧪 Implementato; collaudo completo da confermare |
| **F2C** | Icona personalizzata del progetto Java con varianti chiaro/scuro | ✅ Visualizzazione verificata |
| **F2D** | Icone dedicate ai file Java, Maven, Gradle e correlati | 🧪 Implementato nei sorgenti; verifica grafica completa in corso |
| **F2E** | Source root e validazione package Maven/Gradle/Plain | ✅ Resolver verificati |
| **F2F** | Creazione package, icona, UI WPF, package vuoti e integrazione in Aggiungi → Nuovo elemento | ✅ Collaudato manualmente |
| **F2G.1** | Sette template Java con scelta package e generazione del file nella directory corretta | ✅ Collaudato su tutti e sette i template |
| **F2G.2** | Nuovo package dal wizard dei template, modulo JDK 9+ e cartelle risorse | ✅ Collaudato manualmente |
| **F2H** | Rinomina semantica di package e sottopackage tramite JDT LS, CPS e import | ✅ Collaudato con vincoli (LIM-010–017) |
| **F3.A/F3.A.1** | Target MSBuild Build/Rebuild/Clean e risoluzione eseguibili Maven/Gradle | 🧪 Maven riuscito da MSBuild con path assoluto; autodiscovery e VS Exp ancora da collaudare |
| **U2.3** | Lifecycle JDT LS, chiusura pulita e riuso cache | ✅ Collaudato |
| **U3.A** | Coerenza del modello Java dopo rinomina package | ✅ Collaudato |
| **U3.C.16** | Undo/Redo nativo condiviso, descrizioni toolbar e cleanup | ✅ Collaudato |
| **U4.A–U4.B.1** | Undo/Redo simboli, replay detached e riaggancio CPS/RDT | ✅ Collaudati |
| **U4.C** | Quick Fix, Code Actions e Organize Imports tramite WorkspaceEdit | ✅ Collaudato |
| **U4.D** | Refactoring Java con titoli e Undo/Redo nativo | ✅ Collaudato |
| **Spring Boot** | Integrazione dedicata ai progetti Spring Boot | 📋 Pianificata |
| **Localizzazione** | Interfaccia multilingua dell'estensione (italiano/inglese) | 📋 Pianificata |

### Pack E2 — Inlay Hints (collaudato)

Il **Pack E2** è stato collaudato nell'istanza Visual Studio 2026 Experimental. Gli hint dei **nomi dei parametri** sono ricavati da JDT LS; quelli dei **tipi inferiti con `var`** usano la risposta semantica di JDT LS e, per casi non restituiti dal server, fallback locali limitati e controllati. Sono stati verificati i casi con stringhe (`var text = "Hello"`), costruttori generici (`new ArrayList<String>()`), array (`new String[] { "A" }`) e diamond (`new ArrayList<>()`, tipo `ArrayList<Object>` nel caso verificato). Gli hint sono solo elementi dell'editor: **non vengono inseriti nel file `.java`**. Il progetto di prova è `Examples/InlayHintsAcceptance/`.

### Pack E3 — Indicizzazione, colorazione e prestazioni (collaudato)

Il **Pack E3** e i successivi hotfix di indicizzazione sono stati collaudati manualmente in **Visual Studio 2026 Experimental**. Nel test finale l'apertura dell'editor è risultata responsiva, i colori Java non sono più scomparsi, i simboli della **Navigation Bar**, i conteggi **CodeLens** e gli **Inlay Hints** sono comparsi correttamente; nei log esaminati durante l'ultimo test non sono stati osservati errori.

Le principali misure di stabilizzazione sono:

- **Cache persistente di JDT LS:** i dati dell'indice vengono riutilizzati fra le sessioni. Quando Visual Studio fornisce la cartella *Open Folder*, la cache è separata per percorso del workspace; un blocco esclusivo impedisce a istanze simultanee di usare la stessa directory `-data`.
- **Colorazione semantica:** classificazione lessicale immediata durante l'attesa dei token JDT LS, riuso della scansione sulla stessa versione del documento, pubblicazione dei risultati solo per snapshot validi e tentativi limitati in caso di richieste transitorie fallite.
- **Navigation Bar e CodeLens:** attesa limitata durante l'indicizzazione segnalata da JDT LS, serializzazione delle richieste `textDocument/documentSymbol`, minore concorrenza nelle ricerche dei riferimenti e aggiornamenti quando il workspace torna inattivo.
- **Diagnostica:** in **Strumenti → Java: Stato IntelliSense** sono consultabili stato LSP, attività del workspace, percorso della cache, stato dei Semantic Tokens e informazioni di **Colorazione Java E3**.

I dati della cache risiedono sotto `%LOCALAPPDATA%\JavaLanguageSupportVS\jdtls\persistent\` (con sottocartelle `ws-...`, `slot-...` o fallback `unscoped` se l'identità della cartella non è disponibile). **Non** sono file del progetto e non devono essere aggiunti a Git. Al primo avvio con una nuova cache JDT LS può dover reimportare il progetto; non bisogna considerare `initialize` una prova del completamento dell'indicizzazione.

**Verifica manuale consigliata:** aprire una cartella Java con **File → Apri → Cartella**, attendere l'importazione, controllare colori, Navigation Bar, riferimenti CodeLens e Inlay Hints; poi chiudere e riaprire Visual Studio Experimental con lo stesso workspace e ripetere le prove. Controllare il log JDT LS e **Java: Stato IntelliSense**. Eventuali errori di risorse interne `jdt.ls-java-project` durante l'importazione vanno distinti dai malfunzionamenti persistenti: l'ultimo collaudo è risultato pulito, ma una regressione in altri workspace o versioni di JDT LS resta possibile.

Le indicazioni *collaudato* si riferiscono a verifiche **manuali**, non a test automatici su tutte le versioni di Visual Studio o JDT LS; non costituiscono una garanzia di assenza di regressioni.

### U2–U4 — Lifecycle JDT LS e Undo/Redo globale (collaudati)

Le fasi **U2.3** e **U3.A** stabilizzano l’arresto e il riuso della cache JDT LS e la coerenza del modello dopo la rinomina di package. **U3.C.16** introduce `Editor/Undo/JavaGlobalUndo` e un unico command target per i comandi Annulla/Ripeti nativi di Visual Studio; **U4.A/U4.B/U4.B.1** lo integrano con la rinomina simboli e la riapertura controllata delle schede dopo un cambio fisico di percorso. **U4.C/U4.D** estendono il percorso condiviso a Quick Fix, Code Actions, Organize Imports e refactoring che applicano un `WorkspaceEdit` locale.

**Vincoli da non nascondere:** la cronologia globale è in memoria e non sopravvive a un riavvio di Visual Studio; la registrazione e il replay richiedono verifiche OLE e/o snapshot coerenti; i comandi JDT LS eseguiti senza WorkspaceEdit locale non hanno Undo globale garantito. Per numero massimo di operazioni, limiti sugli snapshot e modalità di blocco, vedere **LIM-021, LIM-022, LIM-030–034 e LIM-040–041** nel [registro dei limiti](docs/IMPLEMENTATION_LIMITS.md).

### Pack F1 e F2 — Project System Java CPS e icone

Il **Pack F1** introduce il tipo di progetto `.javaproj` tramite CPS (Common Project System), limitato alla capability `JavaLanguageSupportCps`. Il file `.javaproj` contiene metadati per Visual Studio: **non sostituisce** `pom.xml` o `build.gradle`, che restano le definizioni di build Maven/Gradle. Il progetto di accettazione è `Examples/JavaProjectSystemAcceptance/HelloJavaF1.javaproj`.

I file del progetto vengono caricati da `ProjectSystem/BuildSystem/JavaProject.targets`: `JavaCompile` include i `**/*.java`, mentre `None` raccoglie gli altri file (per esempio POM, Gradle, risorse, wrapper, README e configurazioni). Sono esclusi automaticamente output e directory temporanee come `bin/`, `obj/`, `target/`, `build/`, `.git/` e `.vs/`. Le inclusioni automatiche sono disattivabili con `EnableDefaultJavaItems=false`. La gestione dei file è integrata in Esplora soluzioni. **F3.A sostituisce il vecchio target `Build` segnaposto con Build/Clean/Rebuild reali**; la compatibilità runtime CPS e le diagnostiche complete richiedono ancora collaudo.

Il **Pack F2A** integra sette modelli nell'interfaccia nativa **Aggiungi → Nuovo elemento**: classe, classe astratta, interfaccia, enum, record (JDK 16+), annotation e file Java vuoto. I modelli sono distribuiti come template VSIX e sono associati alla capability CPS; **F2B** aggiunge le relative icone. Il manifest VSIX registra i template con l'asset `Microsoft.VisualStudio.ItemTemplate`.

#### Pack F2E–F2G — Source root, package e nuovi elementi Java

Il resolver `ProjectSystem/Packages/JavaSourceRootResolver.cs` distingue i progetti **Maven**, **Gradle** e **Java semplici (Plain)**. Riconosce le root convenzionali `src/main/java`, `src/test/java` e, nei progetti semplici, `src/`; considera inoltre alcune root configurate nel POM e i source set Gradle individuati. `JavaPackageResolver.cs` calcola il nome del package a partire dalla root e verifica i segmenti Java. Le directory `resources` **non** sono package Java.

Con **Aggiungi → Nuovo elemento → Package Java** si apre `JavaNewPackageDialog.cs`: è possibile scegliere la root, cercare un package esistente, indicare il nome relativo e verificare l'anteprima della destinazione. Sono supportati package nidificati e directory ancora vuote; `JavaEmptyPackageProjectItems.cs` le registra nel file `.javaproj` tramite `<Folder Include="..." />` per renderle visibili in Esplora soluzioni. La finestra utilizza il tema Visual Studio chiaro/scuro e l'elenco dei package non applica un limite numerico artificiale; l'enumerazione delle cartelle avviene tuttavia in modo sincrono, quindi strutture molto grandi possono richiedere tempo per il caricamento.

I **sette template Java** utilizzano `JavaTemplateWithPackageWizard.cs` e `JavaTemplatePackageSelectDialog.cs`. La finestra permette di scegliere la source root, cercare il package, specificare il nome del nuovo file e visualizzarne il percorso completo. La dichiarazione `package` viene generata in base alla cartella selezionata e il file `.java` viene creato **in quella directory**, senza copie aggiuntive nella cartella inizialmente selezionata. Dal **Pack F2G.2** è anche disponibile il pulsante **Nuovo package...**: dopo la creazione, il nuovo package viene selezionato e si può completare la creazione della classe, interfaccia, enum, ecc.

```text
Aggiungi → Nuovo elemento → Classe Java
    → Seleziona source root / package
    → Nuovo package... (facoltativo)
    → Crea package → Seleziona il package creato
    → Crea classe
```

La finestra *Nuovo elemento* può riapparire quando si annulla un wizard; da lì si può scegliere un altro template o premere **Annulla**. I sette modelli Java e il flusso di creazione dei package nella finestra dei template sono stati **collaudati manualmente** in Visual Studio Experimental.

**Ulteriori template F2G.2:** **Modulo Java (JDK 9+)** genera un `module-info.java` nella root dei sorgenti principali, con dichiarazione `module ... { }` oppure `open module ... { }`; non gestisce ancora automaticamente `requires`, `exports` o la configurazione del module path Maven/Gradle. **Cartella risorse Java** crea o registra `src/main/resources` o `src/test/resources` (percorsi standard Maven e Gradle), senza trattarle come package. Il file `_JavaResources.marker` è un segnaposto **interno al template VSIX** e non deve essere generato nel progetto dell'utente.

Per le icone di Esplora soluzioni, `ProjectSystem/Icons/JavaProjectTreeIconProvider.cs` esporta un `IProjectTreePropertiesProvider` per i soli progetti Java CPS. **F2C** assegna l'icona al nodo principale; **F2D** utilizza `JavaProjectItemIcons.cs` per associare i nomi dei file ai moniker del catalogo `JavaProject.imagemanifest`. Le risorse Light/Dark 16×16 e 32×32 sono incorporate in `JavaLanguageSupport.dll` come risorse WPF; il manifest è registrato come asset `Microsoft.VisualStudio.ImageManifest`. I file non riconosciuti mantengono l'icona predefinita di Visual Studio.

| Categoria icona F2D | File riconosciuti (esempi) |
| --- | --- |
| Java e test Java | `*.java`, `*Test.java`, `*Tests.java`, `*IT.java` |
| Maven | `pom.xml`, `mvnw`, `mvnw.cmd`, `maven-wrapper.properties` |
| Gradle / Kotlin DSL | `build.gradle`, `settings.gradle`, `*.gradle.kts`, `gradle.properties`, `gradlew` |
| Altri linguaggi JVM e pacchetti | `*.kt`, `*.kts`, `*.groovy`, `*.class`, `*.jar`, `*.war`, `*.ear` |
| Configurazioni e strumenti | `*.properties`, `*.yaml`, `*.yml`, `*.toml`, `MANIFEST.MF`, `.project`, `.classpath`, `.iml`, `.gitignore`, `Dockerfile`, `README*` |

#### F2H — Rinomina semantica dei package (collaudata)

Il comando di rinomina package CPS usa JDT LS per proporre modifiche a dichiarazioni `package`, riferimenti e `import`, verifica il progetto e la source root, aggiorna le cartelle e gli item `Folder` del `.javaproj`, salva un backup e registra Undo/Redo nell’infrastruttura condivisa. Gestisce anche package padre e sottopackage con sorgenti Java. Il riaggancio delle schede dopo la rinomina fisica è integrato con U4.B.1. **Non** è ancora un meccanismo generale per spostare tipi tra source root o progetti: file esterni, link/reparse point, file sporchi, UTF-16 e item espliciti non `Folder` possono impedire l’operazione (vedere **LIM-010–017**).

#### Anteprima delle icone personalizzate

Le immagini qui sotto sono **le vere risorse PNG presenti nel repository**, non schermate ricreate dell'interfaccia. Permettono di confrontare le versioni previste per i temi **chiaro** e **scuro**.

| Tipo di nodo/file | Light | Dark |
| --- | :---: | :---: |
| Progetto Java | ![Icona progetto Java Light](JavaLanguageSupport/ProjectSystem/Icons/JavaProject.Light.32.png) | ![Icona progetto Java Dark](JavaLanguageSupport/ProjectSystem/Icons/JavaProject.Dark.32.png) |
| Sorgente Java | ![Java Light](JavaLanguageSupport/ProjectSystem/Icons/Files/JavaSource.Light.32.png) | ![Java Dark](JavaLanguageSupport/ProjectSystem/Icons/Files/JavaSource.Dark.32.png) |
| Test Java | ![Java Test Light](JavaLanguageSupport/ProjectSystem/Icons/Files/JavaTest.Light.32.png) | ![Java Test Dark](JavaLanguageSupport/ProjectSystem/Icons/Files/JavaTest.Dark.32.png) |
| Maven (POM) | ![Maven Light](JavaLanguageSupport/ProjectSystem/Icons/Files/Maven.Light.32.png) | ![Maven Dark](JavaLanguageSupport/ProjectSystem/Icons/Files/Maven.Dark.32.png) |
| Gradle | ![Gradle Light](JavaLanguageSupport/ProjectSystem/Icons/Files/Gradle.Light.32.png) | ![Gradle Dark](JavaLanguageSupport/ProjectSystem/Icons/Files/Gradle.Dark.32.png) |
| Gradle Kotlin DSL | ![Gradle Kotlin Light](JavaLanguageSupport/ProjectSystem/Icons/Files/GradleKotlin.Light.32.png) | ![Gradle Kotlin Dark](JavaLanguageSupport/ProjectSystem/Icons/Files/GradleKotlin.Dark.32.png) |
| Kotlin | ![Kotlin Light](JavaLanguageSupport/ProjectSystem/Icons/Files/Kotlin.Light.32.png) | ![Kotlin Dark](JavaLanguageSupport/ProjectSystem/Icons/Files/Kotlin.Dark.32.png) |
| Configurazioni XML | ![XML Light](JavaLanguageSupport/ProjectSystem/Icons/Files/XmlConfig.Light.32.png) | ![XML Dark](JavaLanguageSupport/ProjectSystem/Icons/Files/XmlConfig.Dark.32.png) |

Per il collaudo visivo in IDE, aprire `HelloJavaF1.javaproj` in **Visual Studio Experimental**, espandere Esplora soluzioni e ripetere il controllo cambiando tema. L'anteprima qui sopra documenta le immagini distribuite; **non equivale a uno screenshot dell'effettiva resa in Visual Studio**.

### F3.A — Build, Rebuild e Clean da Visual Studio (in collaudo)

La patch F3.A sostituisce i tre target segnaposto di `JavaProject.targets` con un import a `JavaProject.NativeBuild.targets`. **Nessuna modifica a JDT LS, ai comandi Java dell'editor o all'Undo CPS.** I target vengono saltati quando `DesignTimeBuild=true` e si eseguono dalla directory del `.javaproj`.

| `JavaBuildSystem` nel `.javaproj` | Build | Rebuild | Clean |
| --- | --- | --- | --- |
| `Maven` | `mvnw.cmd -B -DskipTests package` (o `mvn.cmd`) | `-B -DskipTests clean package` | `-B clean` |
| `Gradle` | `gradlew.bat --console=plain build` (o `gradle.bat`) | `--console=plain clean build` | `--console=plain clean` |
| `Plain` | `javac` su `@(JavaCompile)` verso `.java-vs/classes` | pulizia classi e ricompilazione | rimozione esclusiva di `.java-vs/classes` |

Il wrapper locale, quando presente, è preferito allo strumento nel `PATH`. Per i progetti Plain serve `JAVA_HOME` valido oppure `javac.exe` nel `PATH`, e il file `JavaProject.PlainBuild.ps1` deve essere accanto ai target. Il backend è **esplicito**: il valore predefinito in `JavaProject.props` resta `Maven`; per Gradle/Plain impostare `<JavaBuildSystem>Gradle</JavaBuildSystem>` o `Plain` nel `.javaproj`.

**Limiti:** F3.A esegue strumenti esterni (non include una JVM o Maven/Gradle); un wrapper può eseguire script del progetto. Maven `package` salta i test, Gradle `build` mantiene il comportamento dei task Gradle. Plain compila tutti i `JavaCompile` a ogni Build, senza risolvere automaticamente dipendenze esterne o classpath/moduli Maven/Gradle. Gli errori `javac` di base vengono riformattati in stile MSBuild, ma **diagnostica completa Maven/Gradle nell'Elenco errori, build incrementale e configurazioni avanzate** restano da verificare/implementare. Non considerare F3.A collaudato prima dei test nell'IDE (LIM-001, LIM-007–009).

**Prova CLI locale** (Developer PowerShell for VS 2026, nella cartella del progetto):

```powershell
msbuild .\HelloJavaF1.javaproj /t:Build /p:Configuration=Debug /v:minimal
msbuild .\HelloJavaF1.javaproj /t:Rebuild /v:minimal
msbuild .\HelloJavaF1.javaproj /t:Clean /v:minimal
```

**F3.A.1 — Risoluzione Maven/Gradle e deduplicazione del progetto di prova.**
Il test diagnostico del 15/11/2026 ha confermato `MSB3073` con `call "mvn.cmd"` (uscita 1 in 48 ms), ma **BUILD SUCCESS** con la medesima invocazione MSBuild e il percorso assoluto `C:\ProgramData\chocolatey\lib\maven\apache-maven-3.9.11\bin\mvn.cmd`. F3.A.1 usa `JavaProject.ToolRunner.ps1` per risolvere il percorso effettivo tramite wrapper, PATH, `MAVEN_HOME`/`M2_HOME`/`GRADLE_HOME` e installazioni Chocolatey, quindi lancia il tool e propaga il codice di uscita; non altera `pom.xml`/`build.gradle`. L'esempio `HelloJavaF1.javaproj` elimina gli `Include` F1 ridondanti, conservando i default glob per i nuovi file. **Non è una deduplicazione universale di tutti i `.javaproj` esistenti** (LIM-043). Restano da collaudare il resolver, Clean/Rebuild e l'integrazione da VS Experimental. Gli avvisi di Maven su `sun.misc.Unsafe` sono di una sua libreria e non bloccano la build.

**Proprietà MSBuild per la build Java (`.javaproj`).** Le proprietà usate nei file `.targets` hanno nomi stabili e senza sigle di pack: `JavaBuildSystem`, `JavaMavenCommand`, `JavaGradleCommand`, `JavaCompilerSourceList`, `JavaPlainBuildScript` e `JavaBuildToolRunner`; il target interno di validazione si chiama `JavaValidateBuild`. I target pubblici restano `Build`, `Rebuild` e `Clean`. Se erano presenti override personalizzati dei precedenti `JavaF3MavenCommand` / `JavaF3GradleCommand`, rinominarli in `JavaMavenCommand` / `JavaGradleCommand` (anche quando passati con `/p:`). Le sigle F3 sono mantenute **solo nella documentazione storica**, non nei tag XML o nell'output di compilazione. **Stato:** nomenclatura aggiornata, collaudo VS Experimental dopo rinomina richiesto.

## Prerequisiti

- **Windows** con **Visual Studio 2026** e workload **Sviluppo di estensioni per Visual Studio**.
- **.NET Framework 4.7.2** (target del progetto VSIX) e componenti SDK necessari al ripristino NuGet.
- **JDK 21 o superiore**, compatibile con la versione di JDT LS scelta, con `java` e `javac` disponibili.
- [Eclipse JDT Language Server](https://github.com/eclipse-jdtls/eclipse.jdt.ls), estratto su disco con le directory `plugins/` e `config_win/`.
- **Maven** e/o **Gradle**, soltanto se si vogliono usare questi sistemi di build; sono supportati anche i wrapper dei progetti, quando presenti.

### Configurazione di Java e JDT LS

Impostare su Windows le variabili d'ambiente `JAVA_HOME` e `JDTLS_HOME` (sostituire i percorsi con quelli effettivi):

```powershell
[Environment]::SetEnvironmentVariable(
    'JAVA_HOME', 'C:\Percorso\Del\JDK', 'User'
)
[Environment]::SetEnvironmentVariable(
    'JDTLS_HOME', 'C:\Dev\jdt-language-server', 'User'
)
```

Aprire **un nuovo terminale** e riavviare Visual Studio dopo aver modificato le variabili. Per controllare l'ambiente:

```powershell
.\Scripts\Test-JavaEnvironment.ps1
java -version
javac -version
```

Lo script verifica i prerequisiti; **non** conferma da solo che la sessione LSP sia stata stabilita dentro Visual Studio.

## Compilare e provare l'estensione

1. Aprire `JavaLanguageSupportVS.sln` con **Visual Studio 2026**.
2. Ripristinare i pacchetti NuGet, selezionare **Debug | Any CPU** e compilare con **Ctrl + Shift + B**.
3. Impostare `JavaLanguageSupport` come progetto di avvio e premere **F5**: si apre una **seconda istanza sperimentale** di Visual Studio (`Exp`).
4. Nell'istanza sperimentale scegliere **File → Apri → Cartella** e aprire un progetto in `Examples/` (per esempio `IntelliSenseTest`).
5. Aprire un file `.java` e scegliere **Strumenti → Java: Stato IntelliSense** per osservare avvio di JDT LS, richieste e diagnostica.
6. Per build e test usare **Strumenti → Java: Compila progetto**, **Java: Esegui progetto** o **Java: Esegui test**. I messaggi compaiono nella **finestra Output**.

> **Attenzione:** **F5 nella soluzione VSIX** avvia il debug **dell'estensione**, non un programma Java. Il debugger Java integrato è una funzionalità futura.

### Aggiornare la configurazione Visual Studio Experimental

Se dopo una modifica alla VSIX (in particolare ai template, al file `.imagemanifest` o alle icone) l'istanza **Exp** mostra ancora la versione precedente, **chiudere prima Visual Studio Experimental** e, dopo aver ricompilato/deployato l'estensione, eseguire dal **Developer PowerShell for Visual Studio 2026**:

```powershell
devenv /RootSuffix Exp /UpdateConfiguration
```

Il comando aggiorna la configurazione dell'istanza sperimentale, facendo rileggere le registrazioni delle estensioni; può essere utile dopo modifiche ai manifest delle immagini. **Non ricompila il progetto e non sostituisce il deployment della VSIX**. Riaprire poi `Exp` e verificare le icone nei temi chiaro e scuro.

### Comandi dell'editor

| Operazione nel file Java | Scorciatoia nativa Visual Studio |
| --- | --- |
| Formatta documento | **Ctrl+K, Ctrl+D** |
| Formatta selezione | **Ctrl+K, Ctrl+F** |
| Commenta selezione (righe `//`) | **Ctrl+K, Ctrl+C** |
| Decommenta selezione | **Ctrl+K, Ctrl+U** |

La formattazione usa JDT LS e modifica il buffer aperto **senza salvataggio automatico**. I comandi commenta/decommenta funzionano anche senza connessione LSP; tutte le operazioni supportano Undo/Redo. Le scorciatoie dipendono dalla mappatura standard della tastiera di Visual Studio.

Nel menu **Strumenti** sono presenti, tra gli altri, **Java: Stato IntelliSense**, **Java: Organizza import**, **Java: Compila progetto**, **Java: Esegui progetto** e **Java: Esegui test**. Le due voci Java di formattazione sono state rimosse: il Pack D3 usa i comandi standard di Visual Studio.

Nel menu contestuale dei file Java sono presenti **Java: Rinomina simbolo nel progetto**, **Java: Gerarchia chiamate**, **Java: Gerarchia tipi**, **Java: Estrai metodo**, **Java: Estrai variabile**, **Java: Estrai costante**, **Java: Inline variabile**, **Java: Cambia firma metodo**, **Java: Genera costruttore**, **Java: Genera getter/setter** e **Java: Override/Implement metodi**. La disponibilità dipende dal contesto e dalle capacità di JDT LS.

Per la rinomina Java, **Invio** applica, **Maiusc + Invio** apre l'anteprima ed **Esc** annulla. Le modifiche multi-file collaudate supportano **Ctrl + Z / Ctrl + Y**, inclusa la rinomina fisica del file nel flusso dedicato. Prima di applicare modifiche importanti, verificare l'anteprima.

### Progetti di esempio e collaudo

Aprire **una cartella di esempio alla volta** e attendere l'indicizzazione iniziale di JDT LS.

| Cartella in `Examples/` | Scopo |
| --- | --- |
| `HelloJava/` | File Java singolo |
| `IntelliSenseTest/` | Completamento, Quick Info, Javadoc e import |
| `DiagnosticsAcceptance/` | Diagnostica e Quick Actions |
| `EditorAcceptance/` | Funzioni generali dell'editor, inclusa formattazione |
| `AdvancedNavigationMaven/`, `NativePeekAcceptance/`, `NativeReferencesAcceptance/` | Navigazione, riferimenti e Peek |
| `WorkspaceSyncAcceptance/` | Rinomina della classe e del file, aggiornamento Explorer |
| `WorkspaceEditAcceptance/` | Modifiche multi-file e anteprima |
| `AdvancedRefactoringAcceptance/` | Estrai metodo/variabile/costante e Inline variabile |
| `SignatureThemeAcceptance/` | Cambia firma metodo, parametri e aggiornamento chiamate |
| `MemberGenerationAcceptance/`, `MemberUIAcceptance/` | Costruttori, getter/setter, Override/Implement |
| `CodeLensAcceptance/`, `CodeLensInterfaceAcceptance/` | CodeLens (Pack E1): riferimenti, implementazioni e interfacce |
| `InlayHintsAcceptance/` | Inlay Hints (Pack E2): parametri e tipi dedotti da `var`, inclusi i casi degli hotfix |
| `SemanticTokensAcceptance/`, `NavigationBarAcceptance/`, `EditorReliabilityAcceptance/` | Stabilità della colorazione, recupero dei simboli e verifica dell'editor (Pack E3); ripetere anche dopo il riavvio di VS Experimental |
| `MavenHello/`, `GradleHello/` | Compilazione/esecuzione Maven e Gradle |
| `JavaProjectSystemAcceptance/` | Progetto `HelloJavaF1.javaproj` per collaudo CPS F1, root e package F2E/F2F, sette template F2G, moduli/risorse F2G.2 e icone F2C/F2D |
| `JavaProjectSystemPlainAcceptance/` | Copia dei sorgenti del progetto CPS di prova, backend **Plain/JDK** (`HelloJavaPlain.javaproj`): Build/Clean/Rebuild da collaudare |
| `JavaProjectSystemGradleAcceptance/` | Copia dei sorgenti del progetto CPS di prova, backend **Gradle** (`HelloJavaGradle.javaproj`): Build/Clean/Rebuild da collaudare |

I progetti di accettazione contengono spesso un proprio `README.md` con istruzioni dettagliate. **Alcuni README degli esempi più vecchi possono descrivere comportamenti anteriori agli hotfix**: per lo stato più recente fare riferimento a questa pagina e ai sorgenti correnti.

Per la build del progetto Gradle, il task `run` richiede il plugin `application`. L'esecuzione Maven tramite `exec:java` dipende dall'appropriato plugin Maven e dalle impostazioni del progetto.

## Struttura del repository

La struttura seguente rappresenta **le directory realmente presenti nel repository**, non un layout futuro ipotetico. Sono indicati i moduli principali; l'elenco dei file non è esaustivo.

```text
JavaLanguageSupportVS/
├── JavaLanguageSupportVS.sln
├── README.md
├── .gitattributes
├── .gitignore
├── Scripts/
├── Examples/
├── docs/
└── JavaLanguageSupport/
    ├── Editor/
    │   ├── JavaContentType.cs
    │   ├── Completion/
    │   ├── Braces/
    │   ├── Javadoc/
    │   ├── Intelligence/
    │   ├── Navigation/
    │   ├── Refactoring/
    │   └── Undo/
    ├── LanguageServer/
    ├── Commands/
    ├── Build/
    ├── Configuration/
    ├── Grammars/
    ├── ProjectSystem/
    │   ├── Registration/
    │   ├── BuildSystem/
    │   ├── Packages/
    │   ├── Templates/
    │   └── Icons/
    │       └── Files/
    ├── JavaProject.imagemanifest
    ├── JavaLanguageSupport.csproj
    └── source.extension.vsixmanifest
```

### Responsabilità dei moduli

| Directory | Responsabilità e componenti principali |
| --- | --- |
| `Editor/Completion` | Import suggeriti, gestione delle alternative e azioni correttive |
| `Editor/Braces` | Auto-pair di parentesi, virgolette e opzioni dell'editor |
| `Editor/Javadoc` | Quick Info WPF, Javadoc, tag e continuazione commenti |
| `Editor/Intelligence` | Classificazione semantica, evidenziazione, CodeLens, Inlay Hints |
| `Editor/Navigation` | Definizioni, implementazioni, riferimenti, Ctrl+Click, Peek, gerarchie |
| `Editor/Refactoring` | Rinomina package/simboli, WorkspaceEdit, replay detached, estrazioni, firma e generazione membri |
| `Editor/Undo` | `JavaGlobalUndo`, marker OLE, target nativo condiviso e contratto delle operazioni Java |
| `docs/IMPLEMENTATION_LIMITS.md` | Registro tecnico dei limiti attivi e risolti, con ID, fonti e criteri di verifica |
| `LanguageServer` | Client JDT LS, comunicazione JSON-RPC, diagnostica, stato e attività workspace |
| `Commands` | Registrazione e implementazione dei comandi, `Commands.vsct` |
| `Build` | Rilevamento workspace, build/run/test tramite JDK, Maven e Gradle |
| `Configuration` | Impostazioni Java, temi, TextMate e registrazioni `.pkgdef` |
| `Grammars` | Grammatica `java.tmLanguage.json` |
| `ProjectSystem/Registration` | Registrazione del tipo CPS `.javaproj` e della capability `JavaLanguageSupportCps` |
| `ProjectSystem/BuildSystem` | `.props`, `.targets` e schemi XAML per proprietà, tipi di elementi e inclusione file |
| `ProjectSystem/Packages` | Rilevamento source root, validazione package, creazione delle directory e registrazione degli item CPS `Folder` |
| `ProjectSystem/Templates` | Sorgenti `.vstemplate`, ZIP dei sette modelli Java e dei template Package/Modulo/Risorse, wizard e dialog WPF |
| `ProjectSystem/Icons` | Provider CPS, mappatura moniker per file e PNG Light/Dark |
| `JavaProject.imagemanifest` | Identificatori e sorgenti delle icone di progetto e dei file |

La VSIX usa **.NET Framework 4.7.2** (`net472`) e il progetto C# specifica **`LangVersion 7.3`**. Le modifiche ai sorgenti devono quindi essere compatibili con C# 7.3. Il file `.gitattributes` richiede **CRLF** per i principali file C#, JSON, progetto e configurazione; preservare questo formato quando si applicano patch.

I file `JavaLanguageSupport.csproj` e `source.extension.vsixmanifest` restano nella directory principale `JavaLanguageSupport/`. Non usare i percorsi del vecchio layout *legacy*.

## Diagnostica e problemi comuni

**JDT LS non parte:** verificare `JDTLS_HOME`, le directory `plugins/` e `config_win/`, la versione del JDK e il percorso del log riportato da **Java: Stato IntelliSense**. `JAVA_HOME` è consigliata anche quando Java è nel `PATH`.

**IntelliSense incompleto o refactoring non disponibile:** aprire una *cartella* di progetto Java (non soltanto un file isolato), attendere l'importazione e controllare `textDocument/completion`, `textDocument/hover`, `textDocument/codeAction` e l'attività del workspace. Alcune azioni dipendono da Java, classpath, versione di JDT LS, selezione e indicizzazione.

**Navigation Bar, riferimenti o colori temporaneamente mancanti all'avvio:** JDT LS può essere ancora in fase di importazione Maven/Gradle. Verificare l'avanzamento in **Java: Stato IntelliSense**, attendere il caricamento e controllare che le informazioni compaiano senza riaprire il documento. Il Pack E3 migliora il recupero automatico; per errori persistenti, annotare workspace, versione di JDT LS e messaggi del log prima di valutare un reset della cache.

**Le parentesi o i commenti non si completano:** controllare le opzioni dell'editor, il Content Type `java` e i gestori in `Editor/Braces` e `Editor/Javadoc`. Il **Pack D2 è stato collaudato**; eventuali regressioni vanno verificate nell'istanza Experimental.

**Import ambigui:** posizionare il cursore sul nome del tipo e usare **Ctrl + .**. Le alternative dipendono dal classpath indicizzato; ad esempio `List` può riferirsi a `java.util.List` o `java.awt.List`.

**Anteprima multi-file e Undo/Redo:** le operazioni C1/C2 sono state collaudate nell'istanza Experimental, ma il salvataggio può includere **modifiche preesistenti non salvate** nei documenti coinvolti. È consigliato fare una copia o usare Git prima dei refactoring rilevanti. L'esecutore `WorkspaceEdit` condiviso accetta modifiche testuali su file locali `.java` esistenti; le operazioni generiche di creazione, eliminazione o rinomina di risorse non sono supportate lì. La rinomina del file di una classe usa invece il flusso dedicato C1.

**Gerarchie, CodeLens e Inlay Hints incompleti:** le funzioni semantiche dipendono da JDT LS e dal suo stato di indicizzazione. **E1 ed E2 sono collaudati**, ma la presenza e la completezza dei risultati dipendono dalla versione e dalla risposta del server. E2 include anche fallback locali controllati per alcuni tipi `var` (stringhe, costruttori generici, array e `new ArrayList<>()`), senza alterare il codice Java.

**Comandi Java non visibili:** assicurarsi di usare l'**istanza sperimentale**, verificare l'attivazione della VSIX in **Estensioni → Gestisci estensioni**, poi controllare `ActivityLog.xml` se l'estensione non si carica.

**Icone Java/Maven/Gradle non aggiornate o icona del progetto generica:** verificare che la VSIX includa `JavaProject.imagemanifest`, che l'asset `Microsoft.VisualStudio.ImageManifest` sia registrato in `source.extension.vsixmanifest` e che le PNG siano incorporate nella DLL tramite `UseWPF`. Dopo il deployment, chiudere `Exp` e usare `devenv /RootSuffix Exp /UpdateConfiguration`. Le icone F2D sono specifiche di Esplora soluzioni nei progetti `.javaproj`, non sono associazioni globali a tutte le estensioni in Windows o Visual Studio.

I log locali e i workspace temporanei di JDT LS **non devono essere aggiunti al repository**. Ignorare inoltre `bin/`, `obj/`, `.vs/`, `.java-vs/` e i pacchetti `.vsix` generati.

**Package o template non visibili in «Aggiungi → Nuovo elemento»:** usare l'istanza Experimental, ricompilare e distribuire la VSIX. Se la finestra presenta ancora template o icone vecchi, chiudere l'istanza e applicare `devenv /RootSuffix Exp /UpdateConfiguration`. Per i template, rigenerare gli ZIP con `JavaLanguageSupport/ProjectSystem/Templates/Build-JavaCpsItemTemplates-F2.ps1` quando si modificano i sorgenti `.vstemplate`.

**Package Java e risorse:** la creazione di package richiede una source root Java valida; `src/main/resources` e `src/test/resources` sono cartelle risorse, non package. `_JavaResources.marker` è previsto solo nei sorgenti/archivi del template: se compare nel progetto dell'utente, va trattato come un problema del wizard.

## Limiti conosciuti e registro tecnico

**Registro centralizzato:** [docs/IMPLEMENTATION_LIMITS.md](docs/IMPLEMENTATION_LIMITS.md). Ogni limite ha un **ID stabile**, l'indicazione se è una restrizione intenzionale, una funzionalità parziale o una voce di roadmap, il **file/metodo sorgente** che lo determina, l'impatto e un test di regressione proposto. Le voci risolte restano tracciate con pack e collaudo.

| Area | Principali limiti verificati nel codice | ID nel registro |
| --- | --- | --- |
| Project System / build | **F3.A in collaudo**: MSBuild invoca Maven/Gradle o `javac` Plain; strumenti esterni e output/diagnostica presentano limiti; POM e Gradle DSL sono risolti staticamente | LIM-001–004, LIM-006–009 |
| Template e moduli | `module-info.java` iniziale senza `requires`/`exports` automatici; cartelle risorse convenzionali | LIM-005 |
| Rinomina package CPS | Richiede sorgenti e root riconosciuta; soglie 1.000 cartelle / 10.000 file `.java` / 16 MiB per file; UTF-16, link, file sporchi e item CPS espliciti non gestiti | LIM-010–017 |
| Rinomina simboli | Niente rename file solo maiuscole/minuscole o spostamenti fra directory; replay detached con snapshot e riapertura schede condizionati | LIM-020–022 |
| WorkspaceEdit / refactoring | Solo TextEdit su `.java` locali; massimo 50 documenti, 5.000 edit, 2 MiB/file; replay detached budget 16 MiB; command-only senza Undo globale garantito | LIM-030–034 |
| Undo/Redo condiviso | Cronologia **in memoria**, massimo **32 operazioni**, replay rifiutato dopo cambiamenti non verificati | LIM-040–041 |
| Lifecycle JDT LS | Disponibilità semantica subordinata a importazione e indicizzazione, anche con cache persistente | LIM-042 |
| Funzioni future | Debug Java IDE, Test Explorer, designer visuale, supporto Spring Boot dedicato e localizzazione completa | LIM-050–051 |

**Interpretazione:** i blocchi per file sporchi, destinazioni esterne, cambiamenti successivi e snapshot incoerenti sono **vincoli di sicurezza**, non difetti da aggirare. La chiusura manuale di un editor può restare necessaria quando non è possibile un riaggancio CPS/RDT sicuro (LIM-022). I pack U3/U4 sono collaudati manualmente, ma i test negativi indicati nel registro non devono essere considerati implicitamente collaudati.

## Roadmap

La stabilizzazione dell'editor segue un percorso incrementale che **consolida funzionalità già presenti**, senza riscrivere quelle collaudate.

| Fase | Ambito | Stato |
| --- | --- | --- |
| **A (A1–A3)** | IntelliSense, Quick Info/Javadoc, diagnostica, Quick Actions e import | ✅ Collaudata |
| **B (B1–B2)** | Navigazione, riferimenti, Peek e sorgenti JDK | ✅ Collaudata |
| **C (C1–C3)** | Rinomina, WorkspaceEdit multi-file e refactoring avanzati | ✅ Collaudata |
| **D1** | Generazione costruttori, getter/setter, Override/Implement | ✅ Collaudato |
| **D2** | Parentesi, virgolette, commenti e completamento Javadoc | ✅ Collaudato |
| **D3** | Formattazione e commenti con scorciatoie native di Visual Studio | ✅ Collaudato |
| **E1** | CodeLens, riferimenti e implementazioni | ✅ Collaudato (con hotfix) |
| **E2** | Inlay Hints dei parametri e tipi `var` (incluse stringhe, array, generici e `<>`) | ✅ Collaudato (con hotfix) |
| **E3** | Indicizzazione JDT LS, prestazioni e ripristino automatico dei colori semantici | ✅ Collaudato (con hotfix) |
| **F1** | Project System CPS `.javaproj` e file in Esplora soluzioni | ✅ Collaudato manualmente |
| **F2A–F2B** | Template Java nativi, eliminazione duplicati e icone dei modelli | 🧪 Modelli verificati; collaudo icone dei modelli da completare |
| **F2C** | Icona personalizzata del progetto Java | ✅ Visualizzazione verificata |
| **F2D** | Icone personalizzate per sorgenti Java e file Maven/Gradle | 🧪 Implementato; collaudo visivo da completare |
| **F2E** | Risoluzione source root e nomi package Maven/Gradle/Plain | ✅ Implementato e verificato |
| **F2F** | Nuovo package Java con WPF e nodi `Folder` CPS vuoti | ✅ Collaudato manualmente |
| **F2G.1** | Selezione package per tutti e sette i template Java | ✅ Collaudato manualmente |
| **F2G.2** | Nuovo package durante la creazione dei template, `module-info.java`, directory `resources` | ✅ Collaudato manualmente |
| **F2H** | Rinomina semantica di package e sottopackage con CPS e JDT LS | ✅ Collaudata; LIM-010–017 |
| **F3.A** | Build/Rebuild/Clean nativi CPS via MSBuild con backend Maven, Gradle o JDK Plain | 🧪 Patch preparata; collaudo VS Exp richiesto |
| **F3.B** | Diagnostica completa, configurazioni e miglioramenti build | 📋 Pianificata dopo F3.A |
| **U2.3 / U3.A** | Lifecycle cache JDT LS e coerenza modello dopo rinomina | ✅ Collaudati |
| **U3.C.16 / U4.A–U4.D** | Undo/Redo nativo condiviso, replay e refactoring | ✅ Collaudati; LIM-020–042 |
| **U5** | Consolidamento documentazione, limiti tracciati e futuri miglioramenti CPS | 🔄 In corso |
| **Spring Boot** | Riconoscimento progetti, strumenti Spring e avvio dell'applicazione | 📋 Pianificato |
| **Localizzazione** | Risorse testuali traducibili, italiano/inglese e selezione della lingua | 📋 Pianificata |
| **Integrazione IDE** | Raffinamento build F3.B, dipendenze Maven/Gradle, debugger Java e Test Explorer; debug rimandato al completamento CPS | 📋 Evoluzione futura |
| **Designer visuale** | Swing prima, quindi AWT e JavaFX | 📋 Evoluzione futura |

### Evoluzioni successive

1. Consolidare i comandi di **compilazione, esecuzione e test**, lanciati oggi da **Strumenti → Java**.
2. **Estendere il refactoring package F2H già collaudato** ai casi ancora esclusi dal codice (package senza sorgenti, item CPS espliciti, limiti documentati), senza perdere le protezioni di Undo/Redo; poi evolvere integrazione build e dipendenze Maven/Gradle.
3. Integrare **debug Java** (breakpoint, step, variabili) con il flusso IDE.
4. Integrare **JUnit/TestNG** nel Test Explorer.
5. Progettare un **Designer Swing/AWT/JavaFX** con toolbox, proprietà, gerarchia componenti, anteprima e generazione/sincronizzazione del codice. Nessun designer visuale è attualmente disponibile.
6. Integrare **Spring Boot**: identificare progetti Maven/Gradle con dipendenze Spring Boot, offrire comandi dedicati di avvio/arresto e configurazione, e valutare il supporto a `application.properties`/`application.yml`, annotazioni e Spring Initializr. Queste integrazioni sono **proposte**, non funzionalità già disponibili.
7. Rendere l'estensione **traducibile**: spostare stringhe di comandi, menu, finestre WPF, notifiche ed errori in risorse localizzabili (ad esempio `.resx`), introdurre italiano e inglese, prevedere una lingua di fallback e valutare un'impostazione per scegliere la lingua senza alterare le preferenze di Visual Studio. La localizzazione completa è **pianificata**, non ancora implementata.

### Spring Boot e localizzazione — obiettivi futuri

**Spring Boot:** oggi l'estensione usa JDT LS e gli strumenti Java/Maven/Gradle esistenti, quindi può aprire e lavorare con codice Java appartenente a progetti Spring Boot compatibili. Non include però un'esperienza Spring Boot dedicata (rilevamento del framework, profili Spring, Spring Initializr, comandi di avvio/arresto specifici o assistenza specializzata per le proprietà di configurazione). Il supporto specifico sarà progettato in una fase successiva alla stabilizzazione dell'editor.

**Traduzione dell'estensione:** l'obiettivo è permettere la localizzazione dell'**interfaccia della VSIX**, non tradurre automaticamente il codice Java dell'utente. La prima coppia di lingue prevista è **italiano/inglese**; successivamente potranno essere aggiunte altre traduzioni tramite file di risorse, senza duplicare la logica dell'estensione. I nomi tecnici delle API, i comandi standard di Visual Studio e le risposte di JDT LS non sono necessariamente traducibili dall'estensione.

## Riferimenti

- [Eclipse JDT Language Server](https://github.com/eclipse-jdtls/eclipse.jdt.ls)
- [Language Server Protocol in Visual Studio](https://learn.microsoft.com/en-us/visualstudio/extensibility/adding-an-lsp-extension)
- [Visual Studio Project System](https://github.com/microsoft/VSProjectSystem)
- [Java Debug Server](https://github.com/microsoft/java-debug)

---

**Repository:** [DeveloperForYou/JavaLanguageSupportVS](https://github.com/DeveloperForYou/JavaLanguageSupportVS). Documentazione aggiornata al **15 novembre 2026**, con stato dei collaudi U2–U4 e [registro tecnico dei limiti](docs/IMPLEMENTATION_LIMITS.md). I collaudi riportati sono manuali; nessuna garanzia di compatibilità su tutte le versioni di Visual Studio, JDK, Maven/Gradle e JDT LS.
