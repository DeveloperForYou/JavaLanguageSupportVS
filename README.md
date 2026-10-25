# Java Language Support for Visual Studio 2026

Estensione **VSIX sperimentale** per sviluppare **Java nell'IDE Visual Studio 2026** (non Visual Studio Code). Le funzionalità semantiche si basano su [Eclipse JDT Language Server](https://github.com/eclipse-jdtls/eclipse.jdt.ls) tramite **Language Server Protocol (LSP)**; compilazione, esecuzione e test utilizzano JDK, Maven o Gradle.

> **Stato del progetto (ottobre 2026):** le fasi di stabilizzazione dell'editor **A1–A3, B1–B2 e C1–C3 sono state collaudate manualmente in Visual Studio 2026 Experimental**. L'estensione rimane sperimentale: generazione codice, comportamento dell'editor e formattazione richiedono ulteriori test (Pack D), seguiti da CodeLens, Inlay Hints e prestazioni (Pack E). Non sono ancora presenti un Project System Java nativo, il debug Java integrato o l'integrazione JUnit/TestNG con Test Explorer. I collaudi manuali non equivalgono a una garanzia di compatibilità generale.

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
| Genera costruttori, getter/setter, Override/Implement | Comandi e finestre presenti; **stabilizzazione Pack D1 da completare** |
| Completamento di `()`, `[]`, `{}`, virgolette e Javadoc | Gestori presenti; **verifiche Pack D2 da completare** |
| Formattazione documento / selezione e commenti Java | **Pack D3 da collaudare**: comandi nativi Visual Studio Ctrl+K,D/F/C/U; organizza import resta nel menu Strumenti |
| CodeLens, Inlay Hints e document highlight | Componenti già presenti; **stabilizzazione/performance Pack E da completare** |
| Compilazione, esecuzione e test | Comandi **Strumenti → Java** per JDK, Maven e Gradle |
| Project System Java (`.javaproj`) in Esplora soluzioni | **Non implementato** |
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
| **D1–D3** | Generazione, editing automatico e formattazione | 🔜 Prossima fase |
| **E** | CodeLens, Inlay Hints e prestazioni | ⏳ Da stabilizzare |

Le indicazioni *collaudato* si riferiscono ai test manuali svolti nell'istanza Experimental del progetto; non sono test automatici eseguiti su ogni versione di JDT LS o di Visual Studio. I comandi di generazione e formattazione **esistono già**: i Pack D non partono da zero, ma ne verificano e consolidano il comportamento.

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
| `CodeLensAcceptance/`, `InlayHintsAcceptance/` | CodeLens e suggerimenti inline |
| `MavenHello/`, `GradleHello/` | Compilazione/esecuzione Maven e Gradle |

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

La VSIX usa **.NET Framework 4.7.2** (`net472`) e il progetto C# specifica **`LangVersion 7.3`**. Le modifiche ai sorgenti devono quindi essere compatibili con C# 7.3. Il file `.gitattributes` richiede **CRLF** per i principali file C#, JSON, progetto e configurazione; preservare questo formato quando si applicano patch.

I file `JavaLanguageSupport.csproj` e `source.extension.vsixmanifest` restano nella directory principale `JavaLanguageSupport/`. Non usare i percorsi del vecchio layout *legacy*.

## Diagnostica e problemi comuni

**JDT LS non parte:** verificare `JDTLS_HOME`, le directory `plugins/` e `config_win/`, la versione del JDK e il percorso del log riportato da **Java: Stato IntelliSense**. `JAVA_HOME` è consigliata anche quando Java è nel `PATH`.

**IntelliSense incompleto o refactoring non disponibile:** aprire una *cartella* di progetto Java (non soltanto un file isolato), attendere l'importazione e controllare `textDocument/completion`, `textDocument/hover`, `textDocument/codeAction` e l'attività del workspace. Alcune azioni dipendono da Java, classpath, versione di JDT LS, selezione e indicizzazione.

**Le parentesi o i commenti non si completano:** controllare le opzioni dell'editor, il Content Type `java` e i gestori in `Editor/Braces` e `Editor/Javadoc`. Questi componenti rientrano nella successiva stabilizzazione **Pack D2**.

**Import ambigui:** posizionare il cursore sul nome del tipo e usare **Ctrl + .**. Le alternative dipendono dal classpath indicizzato; ad esempio `List` può riferirsi a `java.util.List` o `java.awt.List`.

**Anteprima multi-file e Undo/Redo:** le operazioni C1/C2 sono state collaudate nell'istanza Experimental, ma il salvataggio può includere **modifiche preesistenti non salvate** nei documenti coinvolti. È consigliato fare una copia o usare Git prima dei refactoring rilevanti. L'esecutore `WorkspaceEdit` condiviso accetta modifiche testuali su file locali `.java` esistenti; le operazioni generiche di creazione, eliminazione o rinomina di risorse non sono supportate lì. La rinomina del file di una classe usa invece il flusso dedicato C1.

**Gerarchie, CodeLens e Inlay Hints incompleti:** queste funzioni mostrano i risultati realmente forniti da JDT LS; il numero di risultati e la disponibilità cambiano con la versione del server e lo stato dell'indicizzazione.

**Comandi Java non visibili:** assicurarsi di usare l'**istanza sperimentale**, verificare l'attivazione della VSIX in **Estensioni → Gestisci estensioni**, poi controllare `ActivityLog.xml` se l'estensione non si carica.

I log locali e i workspace temporanei di JDT LS **non devono essere aggiunti al repository**. Ignorare inoltre `bin/`, `obj/`, `.vs/`, `.java-vs/` e i pacchetti `.vsix` generati.

## Limiti conosciuti

- Il progetto **non** fornisce ancora un Project System Java con integrazione `.javaproj` in Esplora soluzioni.
- Il debugging Java con i comandi nativi dell'IDE e l'integrazione con Test Explorer sono **futuri**.
- Alcune caratteristiche già implementate (generazione, auto-pair, formattazione, CodeLens e Inlay Hints) richiedono **collaudi ulteriori** prima di considerarle stabilizzate.
- La semantica, la navigazione e i refactoring dipendono dalle funzionalità e dalle risposte del server JDT LS; non vengono inventati riferimenti o modifiche quando il server non restituisce risultati.

## Roadmap

La stabilizzazione dell'editor segue un percorso incrementale che **consolida funzionalità già presenti**, senza riscrivere quelle collaudate.

| Fase | Ambito | Stato |
| --- | --- | --- |
| **A (A1–A3)** | IntelliSense, Quick Info/Javadoc, diagnostica, Quick Actions e import | ✅ Collaudata |
| **B (B1–B2)** | Navigazione, riferimenti, Peek e sorgenti JDK | ✅ Collaudata |
| **C (C1–C3)** | Rinomina, WorkspaceEdit multi-file e refactoring avanzati | ✅ Collaudata |
| **D1** | Generazione costruttori, getter/setter, Override/Implement | 🔜 Prossimo |
| **D2** | Parentesi, virgolette, commenti e completamento Javadoc | ⏳ Da stabilizzare |
| **D3** | Formattazione e commenti con scorciatoie native di Visual Studio | 🧪 Da collaudare |
| **E** | CodeLens, Inlay Hints, affidabilità e prestazioni | ⏳ Da stabilizzare |
| **Integrazione IDE** | Runner, Project System Java, debugger, JUnit/TestNG | 📋 Evoluzione futura |
| **Designer visuale** | Swing prima, quindi AWT e JavaFX | 📋 Evoluzione futura |

### Evoluzioni successive

1. Consolidare i comandi di **compilazione, esecuzione e test**, lanciati oggi da **Strumenti → Java**.
2. Realizzare un **Project System Java** per Esplora soluzioni con progetti e dipendenze Maven/Gradle.
3. Integrare **debug Java** (breakpoint, step, variabili) con il flusso IDE.
4. Integrare **JUnit/TestNG** nel Test Explorer.
5. Progettare un **Designer Swing/AWT/JavaFX** con toolbox, proprietà, gerarchia componenti, anteprima e generazione/sincronizzazione del codice. Nessun designer visuale è attualmente disponibile.

## Riferimenti

- [Eclipse JDT Language Server](https://github.com/eclipse-jdtls/eclipse.jdt.ls)
- [Language Server Protocol in Visual Studio](https://learn.microsoft.com/en-us/visualstudio/extensibility/adding-an-lsp-extension)
- [Visual Studio Project System](https://github.com/microsoft/VSProjectSystem)
- [Java Debug Server](https://github.com/microsoft/java-debug)

---

**Repository:** [DeveloperForYou/JavaLanguageSupportVS](https://github.com/DeveloperForYou/JavaLanguageSupportVS). La documentazione descrive i componenti attualmente presenti e distingue i **collaudi manuali completati** dalle **funzionalità sperimentali** e da quelle **ancora pianificate**. Non costituisce una garanzia di compatibilità su tutte le versioni di Visual Studio, JDK e JDT LS.
