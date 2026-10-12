# Java Language Support per Visual Studio 2026

Estensione **VSIX** sperimentale per **Visual Studio IDE** (non Visual Studio Code),
con Java tramite Eclipse JDT Language Server (**JDT LS**).

## Requisiti

- Visual Studio 2026 (con workload **Sviluppo di estensioni per Visual Studio**).
- JDK compatibile con la versione di JDT LS installata, presente nel `PATH` o in `JAVA_HOME`.
- JDT LS estratto e variabile `JDTLS_HOME` impostata sulla cartella che contiene
  `plugins/` e `config_win/`.
- Maven o Gradle per compilare i relativi progetti di esempio (o i wrapper).

## Compilazione e prova

1. Apri `JavaLanguageSupportVS.sln` in Visual Studio 2026.
2. Compila la soluzione in **Debug | Any CPU**.
3. Avvia il progetto VSIX con **F5** (istanza sperimentale, suffisso `Exp`).
4. Nell'istanza sperimentale usa **File > Apri > Cartella** e scegli
   `Examples/IntelliSenseTest`.
5. Apri un file `.java`. Da **Strumenti > Java: Stato IntelliSense**
   controlla la connessione al server e le richieste LSP.

I comandi in **Strumenti** consentono di compilare, eseguire e testare
file singoli e progetti Maven/Gradle; i file di esempio sono in `Examples/`.

## Componenti principali

- `JavaContentType.cs`: registrazione del tipo `.java` nell'editor.
- `JavaLanguageClient.cs`, `JavaLspRpc.cs`: collegamento con JDT LS.
- `JavaQuickInfoProvider.cs`, `JavaImportSuggestedActions.cs`:
  documentazione Javadoc e scelta degli import tramite il server.
- `JavaBraceCompletionProvider.cs`, `JavaEditorBraceOptions.cs` e
  `JavaAutoPairCommandHandler.cs`: parentesi e commenti automatici.
- `JavaJavadocCommandHandler.cs`, `JavaJavadocEnterListener.cs`:
  commenti Javadoc e continuazione delle righe.
- `JavaCommandsPackage.cs`, `JavaTaskExecutor.cs`, `JavaWorkspace.cs`:
  comandi e processo di build/esecuzione.

La configurazione editor TextMate **attiva** e' una sola:
`java-intellisense-language-configuration.json`, referenziata da
`JavaTextMate.pkgdef`. `JavaLanguageSettings.json` contiene invece le
impostazioni LSP/Java. Sono file con responsabilita' differenti.

## Stato e limitazioni

L'editor Java, gli import e la generazione Javadoc sono funzionalita' in
sviluppo e vanno verificate nell'istanza sperimentale. Il supporto C#-equivalente
non e' completo: mancano ancora debugger Java con F5/F9, progetto `.javaproj`
nativo in Esplora soluzioni e integrazione dei test nel Test Explorer.

Non distribuire `bin/`, `obj/`, `.vs/`, i log o il workspace JDT LS in Git.
