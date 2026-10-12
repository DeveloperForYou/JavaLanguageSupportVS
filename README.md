# Java Language Support for Visual Studio 2026

Estensione **VSIX sperimentale** che aggiunge strumenti per lo sviluppo **Java nell'IDE Visual Studio 2026** (non Visual Studio Code). L'analisi del codice è affidata a [Eclipse JDT Language Server](https://github.com/eclipse-jdtls/eclipse.jdt.ls) tramite **Language Server Protocol (LSP)**; la compilazione e l'esecuzione utilizzano JDK, Maven o Gradle.

> **Stato del progetto:** in sviluppo. L'editor Java e le integrazioni LSP sono utilizzabili e vengono migliorati progressivamente, ma **non è ancora disponibile un'esperienza equivalente a C#**: mancano un project system Java nativo e il debugger integrato con F5/F9. Alcune funzioni dell'editor (parentesi, Javadoc e azioni rapide) richiedono una verifica nell'istanza sperimentale dopo ogni modifica.

## Funzionalità

| Area | Stato / comportamento |
| --- | --- |
| Apertura e riconoscimento dei file `.java` | Implementato |
| Colorazione della sintassi Java | Grammatica TextMate inclusa |
| Completamento e suggerimenti Java | Tramite Eclipse JDT LS |
| Diagnostica, navigazione, Quick Info / Javadoc | Integrazione LSP presente; verificare il comportamento per ogni versione |
| Import delle classi, anche con nomi ambigui | Azioni suggerite e scelta del tipo qualificato quando disponibile nel classpath |
| Completamento `()`, `[]`, `{}` e commenti `/* ... */` | Gestori editor dedicati, da verificare in Visual Studio |
| Continuazione Javadoc e tag `@param` / `@return` | Implementazione in evoluzione, da verificare nell'editor |
| Compilazione, esecuzione e test | Comandi **Strumenti → Java** per file singoli, Maven e Gradle |
| Gestione `.javaproj` in Esplora soluzioni | Non implementata |
| Debug Java con breakpoint, variabili e step (F5/F9/F10/F11) | Non implementato |
| JUnit in Test Explorer | Non implementato |

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
4. Nell'istanza sperimentale scegliere **File → Apri → Cartella** e aprire `Examples/IntelliSenseTest` (oppure un progetto Java proprio).
5. Aprire un file `.java`, provare il completamento e selezionare **Strumenti → Java: Stato IntelliSense** per verificare avvio JDT LS, richieste e diagnostica.
6. Per provare la build, aprire un file Java del progetto e usare **Strumenti → Java: Compila progetto**, **Java: Esegui progetto** o **Java: Esegui test**. I messaggi vengono riportati nella **finestra Output**.

> **Attenzione:** il tasto **F5 della soluzione VSIX** avvia il debug **dell'estensione**, non il debugger del programma Java. Il debug Java nativo è una funzionalità futura.

Gli esempi si trovano in `Examples/`:

- `HelloJava/`: file Java singolo;
- `IntelliSenseTest/`: completamento, Javadoc e import;
- `MavenHello/`: progetto Maven;
- `GradleHello/`: progetto Gradle.

Per la build del progetto Gradle, il task `run` richiede il plugin `application`. L'esecuzione Maven tramite `exec:java` richiede l'appropriato plugin Maven; entrambe le operazioni dipendono dalle impostazioni dei progetti di origine.

## Struttura del repository

La struttura seguente corrisponde al **layout previsto dopo il refactoring**. Alcuni file opzionali sono presenti soltanto se sono state integrate le relative patch.

```text
JavaLanguageSupportVS/
├── JavaLanguageSupportVS.sln
├── README.md
├── .gitignore
├── Examples/
├── Scripts/
└── JavaLanguageSupport/
    ├── Editor/
    │   ├── JavaContentType.cs
    │   ├── Completion/
    │   │   └── JavaImportSuggestedActions.cs
    │   ├── Braces/
    │   │   ├── JavaAutoPairCommandHandler.cs
    │   │   ├── JavaBraceCompletionProvider.cs
    │   │   └── JavaEditorBraceOptions.cs         # se presente
    │   └── Javadoc/
    │       ├── JavaQuickInfoProvider.cs
    │       ├── JavaJavadocCommandHandler.cs
    │       └── JavaJavadocEnterListener.cs
    ├── LanguageServer/
    │   ├── JavaLanguageClient.cs
    │   ├── JavaLspRpc.cs
    │   ├── JavaLspDiagnosticsMiddleLayer.cs
    │   └── JavaLanguageStatus.cs
    ├── Commands/
    │   ├── JavaCommandsPackage.cs
    │   └── Commands.vsct
    ├── Build/
    │   ├── JavaWorkspace.cs
    │   └── JavaTaskExecutor.cs
    ├── Configuration/
    │   ├── JavaTextMate.pkgdef
    │   ├── JavaLanguageSettings.json
    │   └── java-*-language-configuration.json  # configurazione attiva
    ├── Grammars/
    │   └── java.tmLanguage.json
    ├── JavaLanguageSupport.csproj
    └── source.extension.vsixmanifest
```

### Responsabilità dei moduli

| Directory | Responsabilità |
| --- | --- |
| `Editor/Completion` | Suggerimenti e azioni per gli import Java |
| `Editor/Braces` | Chiusura automatica delle parentesi e dei commenti |
| `Editor/Javadoc` | Quick Info, continuazione dei commenti, generazione tag Javadoc |
| `LanguageServer` | Avvio e comunicazione con JDT LS, diagnostica LSP |
| `Commands` | Comandi del menu di Visual Studio, registrazione `.vsct` |
| `Build` | Identificazione del workspace e processi esterni di compilazione/test |
| `Configuration` | File di configurazione LSP, TextMate e registrazione `.pkgdef` |
| `Grammars` | Grammatica TextMate per Java |

**Nota tecnica:** `JavaLanguageSupport.csproj` e `source.extension.vsixmanifest` restano nella cartella principale del progetto. Il refactoring sposta i file senza rinominare namespace o classi C#; i percorsi relativi negli asset VSIX devono rimanere coerenti con la nuova struttura.

## Migrazione dalla vecchia struttura

Se stai partendo da un checkout con tutti i sorgenti nella directory `JavaLanguageSupport/`, puoi utilizzare lo script fornito nell'archivio di refactoring (`Refactor-JavaLanguageSupport.py`). Prima **salva le modifiche con un commit** e crea un branch dedicato.

```powershell
git switch -c refactor/project-layout

# Anteprima, senza modifiche
python "C:\Percorso\Refactor-JavaLanguageSupport.py" --repo .

# Esegui la migrazione
python "C:\Percorso\Refactor-JavaLanguageSupport.py" --repo . --apply

git add -A
git diff --cached --stat
git diff --cached --check
```

Lo script esegue `git mv` per i file riconosciuti e aggiorna i riferimenti nel `.csproj`, nel manifest e nel `.pkgdef`; **non esegue commit o push** e non elimina automaticamente file non classificati. Per controllare il pacchetto VSIX dopo la compilazione:

```powershell
python "C:\Percorso\Validate-JavaLanguageSupport-VSIX.py" "C:\Percorso\JavaLanguageSupport.vsix"
```

Sia la compilazione sia l'attivazione dei provider dell'editor devono essere verificate in Visual Studio 2026: il solo controllo dei file nel VSIX non prova che una funzionalità venga richiamata.

## Diagnostica e problemi comuni

**JDT LS non parte:** verificare `JDTLS_HOME`, `plugins/`, `config_win/`, la versione del JDK e il percorso del log riportato da **Java: Stato IntelliSense**. `JAVA_HOME` è consigliata anche quando Java è già nel `PATH`.

**IntelliSense incompleto:** aprire una *cartella* Java, attendere l'indicizzazione, poi controllare le richieste `textDocument/completion`, `textDocument/hover` e `textDocument/codeAction` nel comando **Java: Stato IntelliSense**.

**Le parentesi o i commenti non si completano:** verificare le opzioni dell'editor, il Content Type `java` e il caricamento dei componenti in `Editor/Braces` e `Editor/Javadoc`. La disponibilità dei file nel VSIX da sola non garantisce la registrazione degli handler.

**Import ambigui:** posizionare il cursore sul nome del tipo e usare **Ctrl + .**. Le alternative dipendono dal classpath indicizzato da JDT LS: per esempio `List` può indicare `java.util.List` oppure `java.awt.List`.

**Comandi Java non visibili:** assicurarsi di usare l'**istanza sperimentale** e che l'estensione sia installata e attiva in **Estensioni → Gestisci estensioni**. Se il VSIX viene creato ma non caricato, controllare `ActivityLog.xml` di Visual Studio.

I log locali e i workspace temporanei di JDT LS **non devono essere aggiunti al repository**. Ignorare inoltre `bin/`, `obj/`, `.vs/`, `.java-vs/` e i pacchetti `.vsix` generati.

## Roadmap

1. Stabilizzare l'editor Java: completamento, parentesi/commenti, Javadoc e import ambigui.
2. Integrare Maven e Gradle con gestione progetti e dipendenze più completa.
3. Aggiungere un project system Java per **Esplora soluzioni**, build IDE e modelli di progetto.
4. Integrare un debugger Java con breakpoint, stepping e finestre variabili.
5. Integrare JUnit/TestNG nel **Test Explorer**.

## Riferimenti

- [Eclipse JDT Language Server](https://github.com/eclipse-jdtls/eclipse.jdt.ls)
- [Language Server Protocol in Visual Studio](https://learn.microsoft.com/en-us/visualstudio/extensibility/adding-an-lsp-extension)
- [Visual Studio Project System](https://github.com/microsoft/VSProjectSystem)
- [Java Debug Server](https://github.com/microsoft/java-debug)

---

**Repository:** [DeveloperForYou/JavaLanguageSupportVS](https://github.com/DeveloperForYou/JavaLanguageSupportVS). Questo README descrive il layout previsto dal refactoring e lo stato sperimentale delle funzionalità: non costituisce una dichiarazione di compatibilità o test automatici superati su tutte le versioni di Visual Studio.
