# Java editor — collaudo prima della fase progetti/debugger

Questo pacchetto contiene un progetto Java di prova, **non** una patch VSIX. Aprire `Examples/EditorAcceptance` con `File > Apri > Cartella` nell'istanza sperimentale di Visual Studio 2026.

| Area | Passaggio | Esito atteso |
| --- | --- | --- |
| Completamento | In `EditorSmoke.java` digitare `target.` | Metodi `join`, `greet` e descrizioni senza ritardi evidenti. |
| Signature Help | Inserire il cursore dentro `target.greet(` | Parametri e due overload, evidenziando l'argomento corrente. |
| Quick Info | Hover su `SymbolTarget.join`, `List`, `String` | Descrizione Javadoc; popup scorrevole, blocchi di codice, link. |
| Navigazione | F12 su `join`, Shift+F12 da dichiarazione | Definizione e riferimenti tra file Java. |
| Rename | Rinominare `SymbolTarget.join` in `combine` | Aggiornamento coerente della dichiarazione e del call site, con anteprima se disponibile. |
| Import ambigui | In `ImportChoices.java`, Ctrl+. su `List`, `Date` e `Timer` | Scelta fra tutte le classi omonime disponibili nel classpath. |
| Organize Imports | Richiedere la riorganizzazione da `EditorSmoke.java` | Import ordinati, inutilizzati rimossi, nessuna perdita di compilazione. |
| Format Document | Formattare `FormattingSample.java` | Indentazione/parentesi/spazi standard senza alterare la semantica. |
| Azioni rapide | `ImportChoices.java` su simboli non risolti | Quick fix pertinenti, non solo menu personalizzato degli import. |
| Brackets | In un blocco valido digitare `{`, `(`, `[` e commenti | Chiusure una sola volta, nessuna duplicazione o inserimenti dentro stringhe. |
| Javadoc auto | Scrivere `/**` prima di `greet`, poi Invio | Prefisso ` * `, `@param`, `@return` coerenti e cursore corretto. |
| Navigazione JDK | F12 su `String` o `List` | Aprire sorgenti JDK o vista decompilata read-only; se assente, segnare KO. |
| Document symbols | Aprire Go To Symbol / navigazione | Elenco dei metodi e campi per `SymbolTarget.java`. |
| Diagnostica | Errori in `ImportChoices.java` | Errore sottolineato, Quick Fix, Error List aggiornata. |
| Stabilità | Aprire e chiudere ripetutamente file Java | Nessun duplicato Quick Info o eccezione MEF; memoria stabile. |

Per ogni KO registrare: versione VSIX, Visual Studio 2026, JDK, JDTLS, tasto/menu provato, diagnostica `Java: Stato IntelliSense`, `ActivityLog.xml` se serve.

Nota: `ImportChoices.java` contiene volutamente errori di compilazione. `FormattingSample.java` è volutamente malformattato.
