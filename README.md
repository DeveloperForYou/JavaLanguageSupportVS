# Java Language Support for Visual Studio 2026

Estensione **VSIX sperimentale** per sviluppare **Java nell'IDE Visual Studio 2026** (non Visual Studio Code). Le funzionalità semantiche si basano su [Eclipse JDT Language Server](https://github.com/eclipse-jdtls/eclipse.jdt.ls) tramite **Language Server Protocol (LSP)**; compilazione, esecuzione e test utilizzano JDK, Maven o Gradle.

> **Stato del progetto (novembre 2026):** le fasi editoriali **A1–E3** sono state collaudate manualmente in Visual Studio 2026 Experimental. È inoltre presente un **Java Project System CPS sperimentale** per file `.javaproj` (**Pack F1**): apertura in Esplora soluzioni, gestione dei file, proprietà e inclusione automatica dei sorgenti. Il **Pack F2** aggiunge sette modelli Java nativi (F2A), le relative icone (F2B), un'icona personalizzata per il progetto (F2C, visualizzazione verificata) e moniker personalizzati per sorgenti e file Maven/Gradle/configurazione (F2D, implementati nei sorgenti; collaudo grafico completo ancora da completare). La build Java nativa via MSBuild, il debug Java integrato e JUnit/TestNG in Test Explorer restano da sviluppare. La stabilità dell'editor e i risultati di CPS si riferiscono ai collaudi manuali effettuati, non a tutte le versioni e i workspace.

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
| Rinomina di simboli, classi e file `.java` | **Pack C1 collaudato**: anteprima, selezione delle modifiche, salvataggio e Undo/Redo |
| Modifiche LSP a più file (`WorkspaceEdit`) | **Pack C2 collaudato**: anteprima, applicazione coordinata, salvataggio e Undo/Redo collegati |
| Estrai metodo/variabile/costante, Inline variabile, Cambia firma | **Pack C3 collaudato**; risultati e proposte forniti da JDT LS |
| Genera costruttori, getter/setter, Override/Implement | **Pack D1 collaudato**: comandi e finestre di generazione codice |
| Completamento di `()`, `[]`, `{}`, virgolette e Javadoc | **Pack D2 collaudato** |
| Formattazione documento / selezione e commenti Java | **Pack D3 collaudato**: comandi nativi Visual Studio Ctrl+K,D/F/C/U; organizza import resta nel menu Strumenti |
| CodeLens, Inlay Hints e document highlight | **Pack E1 (CodeLens), E2 (Inlay Hints) ed E3 (stabilità e indicizzazione) collaudati**; document highlight presente |
| Compilazione, esecuzione e test | Comandi **Strumenti → Java** per JDK, Maven e Gradle |
| Progetti Spring Boot | **Supporto dedicato pianificato**; i comandi Maven/Gradle esistenti possono essere usati con progetti Spring Boot compatibili, ma non includono ancora funzionalità specifiche del framework |
| Traduzione dell'interfaccia dell'estensione (localizzazione) | **Pianificata**: separazione delle stringhe dell'interfaccia e scelta della lingua, a partire da italiano e inglese |
| Project System Java (`.javaproj`) in Esplora soluzioni | **Pack F1 implementato e collaudato manualmente**: apertura e struttura CPS, cartelle e file, rinomina, aggiunta/rimozione e proprietà; build MSBuild Java non ancora implementata |
| Aggiungi nuovo elemento — modelli Java | **Pack F2A/F2B**: sette template nativi e relative icone; duplicati eliminati nel collaudo |
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

### Pack F1 e F2 — Project System Java CPS e icone

Il **Pack F1** introduce il tipo di progetto `.javaproj` tramite CPS (Common Project System), limitato alla capability `JavaLanguageSupportCps`. Il file `.javaproj` contiene metadati per Visual Studio: **non sostituisce** `pom.xml` o `build.gradle`, che restano le definizioni di build Maven/Gradle. Il progetto di accettazione è `Examples/JavaProjectSystemAcceptance/HelloJavaF1.javaproj`.

I file del progetto vengono caricati da `ProjectSystem/BuildSystem/JavaProject.targets`: `JavaCompile` include i `**/*.java`, mentre `None` raccoglie gli altri file (per esempio POM, Gradle, risorse, wrapper, README e configurazioni). Sono esclusi automaticamente output e directory temporanee come `bin/`, `obj/`, `target/`, `build/`, `.git/` e `.vs/`. Le inclusioni automatiche sono disattivabili con `EnableDefaultJavaItems=false`. La gestione dei file è integrata in Esplora soluzioni, ma **il target MSBuild `Build` non compila ancora Java**: usare Maven/Gradle o i comandi Java già disponibili.

Il **Pack F2A** integra sette modelli nell'interfaccia nativa **Aggiungi → Nuovo elemento**: classe, classe astratta, interfaccia, enum, record, annotation e file Java vuoto. I modelli sono distribuiti come template VSIX e sono associati alla capability CPS; **F2B** aggiunge le relative icone. Il manifest VSIX registra i template con l'asset `Microsoft.VisualStudio.ItemTemplate`.

Per le icone di Esplora soluzioni, `ProjectSystem/Icons/JavaProjectTreeIconProvider.cs` esporta un `IProjectTreePropertiesProvider` per i soli progetti Java CPS. **F2C** assegna l'icona al nodo principale; **F2D** utilizza `JavaProjectItemIcons.cs` per associare i nomi dei file ai moniker del catalogo `JavaProject.imagemanifest`. Le risorse Light/Dark 16×16 e 32×32 sono incorporate in `JavaLanguageSupport.dll` come risorse WPF; il manifest è registrato come asset `Microsoft.VisualStudio.ImageManifest`. I file non riconosciuti mantengono l'icona predefinita di Visual Studio.

| Categoria icona F2D | File riconosciuti (esempi) |
| --- | --- |
| Java e test Java | `*.java`, `*Test.java`, `*Tests.java`, `*IT.java` |
| Maven | `pom.xml`, `mvnw`, `mvnw.cmd`, `maven-wrapper.properties` |
| Gradle / Kotlin DSL | `build.gradle`, `settings.gradle`, `*.gradle.kts`, `gradle.properties`, `gradlew` |
| Altri linguaggi JVM e pacchetti | `*.kt`, `*.kts`, `*.groovy`, `*.class`, `*.jar`, `*.war`, `*.ear` |
| Configurazioni e strumenti | `*.properties`, `*.yaml`, `*.yml`, `*.toml`, `MANIFEST.MF`, `.project`, `.classpath`, `.iml`, `.gitignore`, `Dockerfile`, `README*` |

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
| `JavaProjectSystemAcceptance/` | Progetto `HelloJavaF1.javaproj` per collaudo di CPS F1, dei template F2A/F2B e delle icone F2C/F2D |

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
└── JavaLanguageSupport/
    ├── Editor/
    │   ├── JavaContentType.cs
    │   ├── Completion/
    │   ├── Braces/
    │   ├── Javadoc/
    │   ├── Intelligence/
    │   ├── Navigation/
    │   └── Refactoring/
    ├── LanguageServer/
    ├── Commands/
    ├── Build/
    ├── Configuration/
    ├── Grammars/
    ├── ProjectSystem/
    │   ├── Registration/
    │   ├── BuildSystem/
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
| `Editor/Refactoring` | Rinomina, WorkspaceEdit, estrazioni, firma metodi, generazione membri |
| `LanguageServer` | Client JDT LS, comunicazione JSON-RPC, diagnostica, stato e attività workspace |
| `Commands` | Registrazione e implementazione dei comandi, `Commands.vsct` |
| `Build` | Rilevamento workspace, build/run/test tramite JDK, Maven e Gradle |
| `Configuration` | Impostazioni Java, temi, TextMate e registrazioni `.pkgdef` |
| `Grammars` | Grammatica `java.tmLanguage.json` |
| `ProjectSystem/Registration` | Registrazione del tipo CPS `.javaproj` e della capability `JavaLanguageSupportCps` |
| `ProjectSystem/BuildSystem` | `.props`, `.targets` e schemi XAML per proprietà, tipi di elementi e inclusione file |
| `ProjectSystem/Templates` | Sorgenti e archivi ZIP dei sette modelli Java nativi |
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

## Limiti conosciuti

- Il **Project System CPS `.javaproj` esiste** (Pack F1), ma la build Java tramite il target MSBuild `Build` non è ancora implementata; usare Maven/Gradle o i comandi Java esistenti.
- I template e le icone di progetto/file sono sperimentali: F2C è stato verificato, mentre la resa completa delle icone dei file F2D nei diversi temi richiede ancora collaudo.
- Il debugging Java con i comandi nativi dell'IDE e l'integrazione con Test Explorer sono **futuri**.
- Non è ancora presente un'integrazione specifica per **Spring Boot**: il supporto Maven/Gradle è generico e non equivale a un riconoscimento o un avvio dedicato delle applicazioni Spring Boot.
- Non è ancora presente un sistema completo di **localizzazione dell'estensione**: molte etichette dei menu, finestre e messaggi sono attualmente definite in italiano nei sorgenti.
- **Pack E2 (Inlay Hints) collaudato manualmente**, inclusi gli hotfix per stringhe, generici espliciti, array e diamond `<>`; possono restare limitazioni nei casi di inferenza che dipendono da JDT LS.
- **Pack E3 collaudato manualmente:** la perdita intermittente dei colori e le anomalie transitorie di recupero di simboli/riferimenti non sono riapparse nell'ultimo collaudo; la prima indicizzazione di progetti nuovi può comunque richiedere tempo e il risultato può variare in base al workspace, a Maven/Gradle e alla versione di JDT LS.
- La semantica, la navigazione e i refactoring dipendono dalle funzionalità e dalle risposte del server JDT LS; non vengono inventati riferimenti o modifiche quando il server non restituisce risultati.

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
| **Spring Boot** | Riconoscimento progetti, strumenti Spring e avvio dell'applicazione | 📋 Pianificato |
| **Localizzazione** | Risorse testuali traducibili, italiano/inglese e selezione della lingua | 📋 Pianificata |
| **Integrazione IDE** | Build Java nativa, debugger Java e JUnit/TestNG in Test Explorer; estensione delle funzioni CPS già presenti | 📋 Evoluzione futura |
| **Designer visuale** | Swing prima, quindi AWT e JavaFX | 📋 Evoluzione futura |

### Evoluzioni successive

1. Consolidare i comandi di **compilazione, esecuzione e test**, lanciati oggi da **Strumenti → Java**.
2. **Evolvere il Project System Java CPS già presente** con build integrata, dipendenze Maven/Gradle e ulteriori operazioni native dell'IDE.
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

**Repository:** [DeveloperForYou/JavaLanguageSupportVS](https://github.com/DeveloperForYou/JavaLanguageSupportVS). La documentazione descrive i componenti presenti sul ramo principale al 3 novembre 2026, distinguendo i **collaudi manuali completati** dalle **funzionalità sperimentali** e da quelle **ancora pianificate**. Non costituisce una garanzia di compatibilità su tutte le versioni di Visual Studio, JDK e JDT LS.
