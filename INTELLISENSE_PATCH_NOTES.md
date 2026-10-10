# Java Language Support 0.6 → correzioni IntelliSense (Visual Studio 2026)

Patch applicabile ai sorgenti **v0.6 originale**, senza sostituire `source.extension.vsixmanifest` o il tuo `README.md`.

## Cosa cambia

- `JavaLspDiagnosticsMiddleLayer.cs` e `JavaLanguageStatus.cs`: capacità LSP (`initialize`), stato del canale RPC, titoli e tipi delle Code Actions; traccia anche errori di applicazione degli import.
- `JavaLanguageClient.cs` + `JavaLspRpc.cs`: usa il JsonRpc della **stessa connessione** VS/JDT LS per richieste Java; non avvia un secondo server.
- `JavaLspDiagnosticsMiddleLayer.cs`: tenta anche il pre-resolve (max 8 suggerimenti, budget 450 ms) per completare la documentazione e gli edit di import nelle proposte IntelliSense, senza bloccare indefinitamente l'editor.
- `JavaQuickInfoProvider.cs`: fallback Quick Info nativo su `textDocument/hover`, con documentazione testuale anche in Markdown. Se la documentazione arriva, il testo viene mostrato in una Quick Info Visual Studio.
- `JavaImportSuggestedActions.cs`: lampadina dell'editor per import proposti da `textDocument/codeAction`; gestisce `CodeAction.edit` e il comando speciale `java.apply.workspaceEdit` **solo se contiene già un WorkspaceEdit**. Applica solamente modifiche al documento aperto, con controlli di versione/range. Non esegue comandi arbitrari né modifica altri file.
- `java-intellisense-language-configuration.json` + pkgdef/csproj: associa la configurazione TextMate con `autoClosingPairs` per `{}`, `()`, `[]`, virgolette. Rimossa l'esportazione del vecchio provider di parentesi per evitare conflitti. Il vecchio JSON resta intatto per compatibilità con patch già applicate.

## Limiti della patch

1. NON è stata compilata né collaudata in Visual Studio 2026 in questo ambiente Linux. Le API sono state verificate sulla documentazione VS SDK, ma eventuali errori di compilazione vanno risolti in Visual Studio Windows.
2. Gli import dipendono dalle risposte JDT LS. Le azioni *command-only* prive di WorkspaceEdit già presente non vengono eseguite; le modifiche multi-file vengono rifiutate deliberatamente. Ctrl+. può richiedere un breve aggiornamento della lampadina perché `GetSuggestedActions` non blocca il thread UI in attesa della risposta LSP.
3. Il tooltip Javadoc viene mostrato come testo/Markdown semplice, senza rendering rich HTML. Il `completionItem/resolve` è tentato su un numero limitato di voci; non si garantisce che Visual Studio presenti la documentazione o applichi `additionalTextEdits` in tutti i casi.
4. Se JDT LS non propone import senza diagnostica nel contesto Code Action, il sistema di suggerimenti può risultare vuoto; si dovranno passare le diagnostiche del documento.
5. Non è stato implementato F5/debug Java né un sistema `.javaproj`.

## Installazione / test

```powershell
# NELLA CARTELLA che contiene JavaLanguageSupportVS.sln
# Salvare o committare le modifiche locali prima.
git apply --check "C:\Percorso\JavaLanguageSupport_v0.6_intellisense_completa.patch"
git apply "C:\Percorso\JavaLanguageSupport_v0.6_intellisense_completa.patch"
git diff --check
```

Se hai già applicato la precedente patch di diagnostica, prima esegui `git apply -R` usando quella patch **solo se** corrisponde esattamente ai tuoi file. La correzione precedente delle parentesi non causa conflitti con questa patch perché introduce una configurazione nuova, lasciando invariata la vecchia.

Ricompila (`Ctrl+Shift+B`), premi `F5`, apri `Examples/IntelliSenseTest` con **Apri cartella** e prova:

- `{`, `(`, `[` ⇒ corrispondenti chiusure automatiche;
- `println` ⇒ mouse / `Ctrl+K, Ctrl+I` ⇒ Quick Info Javadoc;
- `ArrayList<String> v = new ArrayList<>();` senza import, cursore su `ArrayList`, `Ctrl+.` ⇒ lampadina `Import ...` se fornita da JDT LS;
- Strumenti → Java: Stato IntelliSense ⇒ rapporto richieste/capacità LSP e connessione `RPC per Quick Info e import: collegato`.

Il manifest resta con la versione corrente per evitare conflitti Git. Se desiderato puoi aumentare manualmente **solo** il suo numero di versione, senza cambiare Identity Id.
