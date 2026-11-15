# Registro dei limiti di implementazione — JavaLanguageSupportVS

**Revisione documentale:** 15 novembre 2026 · **Ambito:** Visual Studio 2026, estensione Java, CPS `.javaproj`, JDT LS, Undo/Redo U2–U4.

Questo documento è il **registro tecnico dei limiti attivi**. Integra il [README principale](../README.md): non sostituisce i collaudi manuali e non trasforma i limiti in bug già riprodotti. Le condizioni elencate sono state riscontrate nei sorgenti del repository consultati per questa revisione; dove si parla di funzionalità futura, la voce è contrassegnata come **roadmap**. Alcuni limiti sono deliberati per impedire modifiche parziali o perdita di dati.

## Come mantenere il registro

- **ID stabile:** non riutilizzare né cancellare un identificativo quando un vincolo viene risolto; spostarlo in «Limiti risolti» con pack, data e prova.
- **Stato:** `ATTIVO / SICUREZZA` (controllo intenzionale), `ATTIVO / FUNZIONALE` (funzione parziale/assente), `ROADMAP` (non implementato), `RISOLTO` (solo dopo verifica).
- **Tracciabilità:** aggiornare *insieme* descrizione, file/metodo sorgente, impatto e prova ogni volta che cambia un controllo nel codice. I link alle righe sono indicativi e possono slittare; i nomi dei metodi sono il riferimento stabile.
- **Collaudo:** «Pack collaudato» indica la verifica manuale comunicata per quella funzionalità, **non** che ogni caso limite sia stato esercitato. I test negativi qui indicati sono casi da verificare, non risultati già certificati.
- **Nuovi limiti:** per qualsiasi `NotSupportedException`, soglia hard-coded, rifiuto di `WorkspaceEdit` o fallback che disabilita una funzione, aggiungere o aggiornare una voce prima di chiudere il pack.

## Project System, build e source root

### LIM-001 — `.javaproj` non compila Java con MSBuild
**Stato:** `ATTIVO / FUNZIONALE` · **Impatto:** il CPS fornisce il progetto in Esplora soluzioni, ma il target nativo MSBuild `Build` non equivale a `javac`/Maven/Gradle. Usare i comandi Java o la build esterna.

**Traccia:** [`JavaProject.targets`](../JavaLanguageSupport/ProjectSystem/BuildSystem/JavaProject.targets) (`JavaCompile`/`None`; nessun task compilatore Java); [README Project System](../JavaLanguageSupport/ProjectSystem/README.md) e README principale. **Verifica:** aprire `.javaproj`, confrontare inclusione file e compilazione tramite Maven/Gradle.

### LIM-002 — Maven source root: lettura statica e proprietà limitate
**Stato:** `ATTIVO / FUNZIONALE` · **Impatto:** il resolver legge `build/sourceDirectory` e `build/testSourceDirectory` dal POM e sostituisce solo `${project.basedir}` e `${basedir}`; altre espressioni `${...}` vengono rifiutate e si torna alle root convenzionali. Non esegue Maven né calcola l'effective POM.

**Traccia:** [`JavaSourceRootResolver.ReadMavenSourceDirectory`](../JavaLanguageSupport/ProjectSystem/Packages/JavaSourceRootResolver.cs#L261-L307). **Verifica:** POM con root letterale, con `${basedir}` e con property personalizzata; verificare il comportamento di fallback.

### LIM-003 — Gradle source set personalizzati non risolti dal DSL
**Stato:** `ATTIVO / FUNZIONALE` · **Impatto:** il resolver riconosce `src/main/java`, `src/test/java` e `src/<sourceSet>/java` **se la directory esiste**. `java.srcDirs` definiti in Groovy/Kotlin DSL richiedono un hint/root esplicito; la configurazione Gradle non viene valutata.

**Traccia:** [`JavaSourceRootResolver.Discover`](../JavaLanguageSupport/ProjectSystem/Packages/JavaSourceRootResolver.cs#L79-L110), argomento `additionalRoots`. **Verifica:** source set convenzionale, aggiuntivo esistente, `java.srcDirs` non convenzionale.

### LIM-004 — Source root esterne al progetto escluse
**Stato:** `ATTIVO / SICUREZZA` · **Impatto:** root arbitrarie fuori dalla directory progetto vengono ignorate; il resolver non promette package Java in percorsi esterni.

**Traccia:** [`JavaSourceRootResolver.AddRoot`](../JavaLanguageSupport/ProjectSystem/Packages/JavaSourceRootResolver.cs#L228-L259). **Verifica:** root relativa `..\` o assoluta esterna e root interna valida.

### LIM-005 — `module-info.java` e risorse: supporto iniziale
**Stato:** `ATTIVO / FUNZIONALE` · **Impatto:** il template modulo crea il descrittore, ma non genera `requires`/`exports` né configura il module path Maven/Gradle; le risorse sono create nei percorsi convenzionali `src/main/resources` e `src/test/resources`, non in source set personalizzati.

**Traccia:** sezione F2G.2 del [README principale](../README.md) e implementazioni in [`ProjectSystem/Templates`](../JavaLanguageSupport/ProjectSystem/Templates). **Verifica:** creare modulo aperto/standard; controllare la configurazione esterna della build.

### LIM-006 — Icone file limitate al CPS, collaudo visivo ancora parziale
**Stato:** `ATTIVO / FUNZIONALE` · **Impatto:** le icone Java/Maven/Gradle custom vengono associate ai nodi del Project System con capability `JavaLanguageSupportCps`, non a ogni file aperto in Visual Studio o all'associazione Windows. La resa effettiva F2D su tutti i temi resta da verificare; la presenza delle PNG nella VSIX non basta.

**Traccia:** [`JavaProjectTreeIconProvider`](../JavaLanguageSupport/ProjectSystem/Icons/JavaProjectTreeIconProvider.cs#L10-L43), [`JavaProjectItemIcons`](../JavaLanguageSupport/ProjectSystem/Icons/JavaProjectItemIcons.cs). **Verifica:** file `.java`, `pom.xml`, `build.gradle` nel `.javaproj` in tema Light/Dark e nella vista cartella non CPS.

## Rinomina semantica package CPS (F2H, U3.A, U3.C)

### LIM-010 — Package privo di sorgenti Java: rinomina semantica rifiutata
**Stato:** `ATTIVO / FUNZIONALE` · **Impatto:** il package selezionato può essere vuoto **solo se contiene sottopackage con sorgenti**; senza alcun `.java` discendente il comando semantico non procede. Per sole cartelle vuote rimane Esplora soluzioni, senza promessa di aggiornamento semantico.

**Traccia:** [`JavaRenamePackageCommand.CollectPackageJavaSources`](../JavaLanguageSupport/ProjectSystem/Packages/JavaRenamePackageCommand.cs#L320-L362). **Verifica:** package con 1 classe, package padre vuoto con classe discendente, albero completamente vuoto.

### LIM-011 — Rinomina package subordinata all'indicizzazione della source root
**Stato:** `ATTIVO / SICUREZZA` · **Impatto:** quando JDT LS non restituisce modifiche, l'estensione può proseguire solo dopo aver verificato `java.project.listSourcePaths`; non assume che `initialize` significhi indicizzazione completa. Riferimenti residui causano un blocco.

**Traccia:** [`JavaRenamePackageCommand.VerifySourceRootIndexedAsync`](../JavaLanguageSupport/ProjectSystem/Packages/JavaRenamePackageCommand.cs#L285-L312), `ExecuteAsync`, `CheckNoStalePackageReferences`. **Verifica:** root non indicizzata, JDT LS inizializzato, package senza riferimenti esterni.

### LIM-012 — Soglie di scansione del package e dei riferimenti
**Stato:** `ATTIVO / SICUREZZA` · **Impatto:** massimo **1.000 directory** nell'albero package; durante il controllo dei riferimenti massimo **10.000 file `.java`** nel progetto e **16 MiB per singolo sorgente**. Oltre tali soglie la rinomina viene annullata, non applicata parzialmente.

**Traccia:** [`CollectPackageJavaSources`](../JavaLanguageSupport/ProjectSystem/Packages/JavaRenamePackageCommand.cs#L329-L335) e [`CheckNoStalePackageReferences`](../JavaLanguageSupport/ProjectSystem/Packages/JavaRenamePackageCommand.cs#L595-L625). **Verifica:** valori di soglia con fixture apposite (non eseguire su produzione senza backup).

### LIM-013 — Collegamenti simbolici/junction non supportati nella rinomina package
**Stato:** `ATTIVO / SICUREZZA` · **Impatto:** cartelle o sorgenti con attributo `ReparsePoint` sono rifiutati, per evitare modifiche fuori dal perimetro verificato.

**Traccia:** [`CollectPackageJavaSources`](../JavaLanguageSupport/ProjectSystem/Packages/JavaRenamePackageCommand.cs#L330-L353), `CheckNoStalePackageReferences`. **Verifica:** symlink/junction di test all'interno del package.

### LIM-014 — Codifica UTF-16 rifiutata dalla rinomina package
**Stato:** `ATTIVO / FUNZIONALE` · **Impatto:** il flusso CPS usa UTF-8 rigoroso, con o senza BOM, e rifiuta BOM UTF-16 LE/BE; non converte silenziosamente la codifica.

**Traccia:** [`JavaRenamePackageCommand.ReadUtf8`](../JavaLanguageSupport/ProjectSystem/Packages/JavaRenamePackageCommand.cs#L719-L735). **Verifica:** `.java` UTF-8 BOM/no BOM e `.java` UTF-16.

### LIM-015 — Item espliciti `.javaproj` sotto il package
**Stato:** `ATTIVO / FUNZIONALE` · **Impatto:** la migrazione CPS aggiorna gli item `Folder` espliciti, ma blocca la rinomina se il `.javaproj` contiene item espliciti di altro tipo (`JavaCompile`, `None`, ecc.) sotto il vecchio percorso; non riscrive arbitrariamente il file di progetto.

**Traccia:** [`JavaRenamePackageCommand.UpdateFolderEntries`](../JavaLanguageSupport/ProjectSystem/Packages/JavaRenamePackageCommand.cs#L737-L777). **Verifica:** `Folder Include` e `JavaCompile Include` manuale nel progetto.

### LIM-016 — Documenti sporchi o non Java aperti: blocco preventivo
**Stato:** `ATTIVO / SICUREZZA` · **Impatto:** prima della rinomina package occorre salvare i `.java` interessati; un `.javaproj` aperto nell'editor e un file non Java aperto dentro la cartella da spostare impediscono il commit. Non vengono scartate modifiche dell'utente.

**Traccia:** [`JavaRenamePackageCommand.EnsureReady`](../JavaLanguageSupport/ProjectSystem/Packages/JavaRenamePackageCommand.cs#L780-L826). **Verifica:** file Java sporco, POM aperto nella cartella, `.javaproj` aperto.

### LIM-017 — Rinomina package locale, non trasferimento arbitrario fra progetti
**Stato:** `ATTIVO / SICUREZZA` · **Impatto:** sono ammesse soltanto operazioni LSP che corrispondono al cambio di percorso atteso all'interno dello stesso progetto/root; create/delete generici e file esterni vengono rifiutati (eccezione specifica per `.temp` JDT LS). Il comando non è un «Move Type» cross-project.

**Traccia:** [`JavaRenamePackageCommand.Prepare`](../JavaLanguageSupport/ProjectSystem/Packages/JavaRenamePackageCommand.cs#L395-L477) e `CheckNoStalePackageReferences`. **Verifica:** refactoring dello stesso package e proposta LSP con spostamento esterno.

## Rinomina simboli Java (C1, U4.A/U4.B/U4.B.1)

### LIM-020 — Rinomina file `.java` soltanto nella stessa cartella
**Stato:** `ATTIVO / FUNZIONALE` · **Impatto:** l'editor rifiuta la rinomina che cambia **solo maiuscole/minuscole**, i trasferimenti fra cartelle/package, `create/delete` generici e destinazioni già esistenti. Il flusso rinomina simboli non sostituisce la rinomina package CPS.

**Traccia:** [`JavaProjectRenameEdits.Prepare`](../JavaLanguageSupport/Editor/Refactoring/JavaInlineRename.cs#L621-L647) e [`JavaProjectRenameEdits.Prepare`](../JavaLanguageSupport/Editor/Refactoring/JavaInlineRename.cs#L795-L813). **Verifica:** `Customer.java`→`Client.java`, `Customer`→`customer`, destinazione esistente, percorso differente.

### LIM-021 — Replay detached della rinomina simboli con snapshot limitati
**Stato:** `ATTIVO / SICUREZZA` · **Impatto:** Undo/Redo dopo la chiusura delle schede è **solo nella sessione VS** e viene abilitato se i file iniziali erano puliti/salvati, coerenti con il disco, nello stesso progetto e senza link; il limite è **256 target** e **256 spostamenti**, con budget **16 MiB** per le copie prima/dopo. Se i file cambiano, il replay viene rifiutato.

**Traccia:** [`SymbolRenameDetachedState.TryCaptureBefore`](../JavaLanguageSupport/Editor/Refactoring/JavaSymbolDetachedUndo.cs#L24-L115), `CompleteAfterSave`, `VerifyOnDisk`. **Verifica:** sorgenti salvati, file inizialmente sporco, modifica successiva, file oltre budget.

### LIM-022 — Riaggancio CPS/RDT dopo rinomina: best effort
**Stato:** `ATTIVO / SICUREZZA` · **Impatto:** la riapertura automatica per correggere `itemId` e navigazione viene tentata solo quando l'Undo detached è sicuro; coinvolge al massimo **128 schede**, rifiuta schede sporche o doppie e può richiedere riapertura manuale se DTE fallisce. Un errore di riapertura non annulla una rinomina già confermata.

**Traccia:** [`JavaRenamedDocumentReopen.RefreshCore`](../JavaLanguageSupport/Editor/Refactoring/JavaRenamedDocumentReopen.cs#L84-L180), [`JavaInlineRename.Apply`](../JavaLanguageSupport/Editor/Refactoring/JavaInlineRename.cs), [`JavaRenamePackageCommand.ExecuteAsync`](../JavaLanguageSupport/ProjectSystem/Packages/JavaRenamePackageCommand.cs#L186-L194). **Verifica:** scheda pulita, due finestre sullo stesso file, scheda sporca, salvataggio e CTRL+Click post-rinomina.

## WorkspaceEdit, Quick Fix, Code Actions e refactoring U4.C/U4.D

### LIM-030 — Esecutore `WorkspaceEdit` limitato ai `TextEdit` locali
**Stato:** `ATTIVO / FUNZIONALE` · **Impatto:** l'esecutore condiviso accetta modifiche testuali su file `.java` **locali ed esistenti**; rifiuta `create/rename/delete`, operazioni di risorsa e payload con `changes` e `documentChanges` contemporaneamente. Le rinomine fisiche usano gli esecutori dedicati.

**Traccia:** [`JavaWorkspaceEditService.Parse`](../JavaLanguageSupport/Editor/Refactoring/JavaWorkspaceEditService.cs#L97-L160). **Verifica:** TextEdit singolo/multi-file, `RenameFile`, `CreateFile`, URI non `file://`.

### LIM-031 — Soglie `WorkspaceEdit`: documenti, modifiche e dimensione
**Stato:** `ATTIVO / SICUREZZA` · **Impatto:** massimo **50 documenti**, **5.000 modifiche testuali** e **2 MiB per singolo `.java`**; l'operazione viene rifiutata prima dell'applicazione se supera i controlli.

**Traccia:** [`JavaWorkspaceEditService`](../JavaLanguageSupport/Editor/Refactoring/JavaWorkspaceEditService.cs#L33-L37), `Parse`, `Prepare` (verifica `FileInfo.Length`). **Verifica:** dati alla soglia e oltre soglia, sovrapposizioni di edit.

### LIM-032 — Replay detached WorkspaceEdit con budget 16 MiB
**Stato:** `ATTIVO / SICUREZZA` · **Impatto:** Undo/Redo dopo la chiusura delle schede richiede snapshot dei file **prima e dopo** l'operazione, con budget complessivo **16 MiB**, documenti salvati, assenza di reparse point e byte invariati. Se la cattura non è sicura, resta Undo nativo locale finché il buffer è valido; non è promesso il replay detached.

**Traccia:** [`WorkspaceEditDetachedState.TryCaptureBefore`](../JavaLanguageSupport/Editor/Refactoring/JavaWorkspaceEditService.DetachedUndo.cs#L21-L82), `CompleteAfterSave`, `VerifyDisk`, `PrepareTabs`. **Verifica:** salvataggio iniziale, modifica successiva, duplicazione delle schede, file readonly/symlink.

### LIM-033 — `workspace/executeCommand` senza WorkspaceEdit locale non ha Undo globale garantito
**Stato:** `ATTIVO / FUNZIONALE` · **Impatto:** se un'azione JDT LS espone solo un comando eseguito dal server, il client non applica una transazione locale `WorkspaceEdit` e quindi non registra automaticamente un'operazione `JavaGlobalUndo`. Il log deve segnalarlo; non interpretare il messaggio come errore della Code Action.

**Traccia:** [`JavaEditorLspOperations.ApplySuggestedCodeActionAsync`](../JavaLanguageSupport/Editor/Refactoring/JavaEditorLspOperations.cs#L242-L297) (U4.D). **Verifica:** Code Action con `edit`, con `codeAction/resolve`, e azione *command-only*.

### LIM-034 — Refactoring condizionati da JDT LS
**Stato:** `ATTIVO / FUNZIONALE` · **Impatto:** Extract Method/Variable/Constant, Inline Variable, Change Signature e generazione membri si appoggiano a operazioni LSP o comandi specifici JDT LS. Se il server non propone un'azione valida, l'estensione non fabbrica modifiche semantiche alternative. L'Undo globale U4.D si applica alle operazioni che producono un `WorkspaceEdit` locale.

**Traccia:** [`JavaExtractRefactorings.ExecuteAsync`](../JavaLanguageSupport/Editor/Refactoring/JavaExtractRefactorings.cs), [`JavaAdvancedRefactorings.ExecuteAsync`](../JavaLanguageSupport/Editor/Refactoring/JavaAdvancedRefactorings.cs), [`JavaEditorLspOperations.ApplySuggestedCodeActionAsync`](../JavaLanguageSupport/Editor/Refactoring/JavaEditorLspOperations.cs). **Verifica:** caso con proposta JDT LS, caso con nessuna proposta o azione disabilitata.

## Undo/Redo nativo, lifecycle e funzionalità future

### LIM-040 — Undo globale circoscritto alla sessione e alle ultime 32 operazioni
**Stato:** `ATTIVO / FUNZIONALE` · **Impatto:** `JavaGlobalUndo` mantiene una lista **in memoria** di massimo **32 operazioni**; non serializza la cronologia tra riavvii di Visual Studio. Il routing usa un marker nel manager OLE nativo del buffer; quando la verifica della transazione fallisce, il bridge lascia la precedenza al comportamento Undo dell'editor.

**Traccia:** [`JavaGlobalUndo`](../JavaLanguageSupport/Editor/Undo/JavaGlobalUndo.cs#L21-L128), `Candidate`, `AreNativeStacksEmpty`. **Verifica:** Undo locale prioritario, 33+ operazioni, chiusura/riapertura VS, descrizioni native.

### LIM-041 — Replay detached: sicurezza prima della disponibilità
**Stato:** `ATTIVO / SICUREZZA` · **Impatto:** un Undo/Redo detached non può sovrascrivere file cambiati dopo il refactoring; il codice esegue una verifica approfondita di byte, percorso e documenti aperti **prima** della scrittura. La disabilitazione in questi casi è prevista, non una regressione delle frecce.

**Traccia:** [`JavaSymbolDetachedUndo`](../JavaLanguageSupport/Editor/Refactoring/JavaSymbolDetachedUndo.cs), [`JavaWorkspaceEditService.DetachedUndo`](../JavaLanguageSupport/Editor/Refactoring/JavaWorkspaceEditService.DetachedUndo.cs), [`JavaRenamePackageCommand.DetachedUndo`](../JavaLanguageSupport/ProjectSystem/Packages/JavaRenamePackageCommand.DetachedUndo.cs). **Verifica:** modificare e salvare un file dopo il commit, quindi tentare Undo da altra scheda.

### LIM-042 — Il completamento dell'indicizzazione JDT LS non coincide con `initialize`
**Stato:** `ATTIVO / OPERATIVO` · **Impatto:** cache persistente e shutdown pulito riducono le anomalie, ma un nuovo workspace o un cambio di classpath richiedono l'importazione JDT LS. Navigazione, riferimenti e refactoring possono non essere disponibili durante l'indicizzazione.

**Traccia:** diagnostica JDT LS in [`LanguageServer`](../JavaLanguageSupport/LanguageServer) e controllo esplicito in [`VerifySourceRootIndexedAsync`](../JavaLanguageSupport/ProjectSystem/Packages/JavaRenamePackageCommand.cs#L285-L312). **Verifica:** prima apertura di un progetto nuovo, riavvio con cache riutilizzata, variazione POM/Gradle.

### LIM-050 — Debug Java, Test Explorer e designer visuale assenti
**Stato:** `ROADMAP` · **Impatto:** i comandi Java di build/run/test non equivalgono a debugging F5/F9 o all'integrazione di JUnit/TestNG nel Test Explorer; non è disponibile un designer Swing/AWT/JavaFX. **Fonte:** roadmap del [README principale](../README.md), non dedotto dalla sola assenza di un singolo metodo.

### LIM-051 — Integrazione Spring Boot e localizzazione completa assenti
**Stato:** `ROADMAP` · **Impatto:** il supporto a Maven/Gradle non fornisce strumenti Spring Boot dedicati; UI italiana/inglese basata su risorse di localizzazione e scelta lingua non è completata. **Fonte:** roadmap del [README principale](../README.md).

## Limiti risolti (conservare gli ID)

| ID | Problema precedente | Correzione/pack | Riscontro |
| --- | --- | --- | --- |
| **FIX-001** | `itemId` CPS obsoleto dopo rinomina fisica, con salvataggio/navigazione bloccati fino a riapertura manuale | U4.B.1 (riaggancio controllato delle schede, `JavaRenamedDocumentReopen`) | **Collaudato manualmente**; vedere anche il limite residuo **LIM-022** |
| **FIX-002** | Rinomina package con singolo sorgente e riferimenti/navigazione non coerenti | U3.A (controlli di modello e fallback dichiarazione locale) | **Collaudato manualmente**; vedere **LIM-011** per le condizioni attuali |
| **FIX-003** | Descrizioni «Dummy Item» nelle tendine native Undo/Redo | U3.C.15, consolidato da U3.C.16 | **Collaudato manualmente**; vedere **LIM-040** |

## Checklist minima per nuovi pack

1. Esaminare *anche i percorsi di rifiuto*: `NotSupportedException`, controlli `IsDirty`, soglie numeriche, validazioni di root/path, snapshot e fallback.
2. Indicare nel README se il nuovo comportamento è **implementato**, **collaudato manualmente**, **parziale**, oppure **pianificato**.
3. Aggiornare il registro conservando ID, file/metodo, impatto, test negativo e stato.
4. Se il limite è stato risolto, registrare **pack, data e risultato del collaudo** invece di cancellarne la storia.
5. Per modifiche al codice preservare la struttura `JavaLanguageSupport/Editor/…`, `ProjectSystem/…` e i CRLF richiesti da `.gitattributes`.
