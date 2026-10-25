# CodeLens — test editor Java

Apri la cartella Maven in Visual Studio 2026, attendi la fine dell'import del progetto
ed apri `Formatter.java` e `AbstractFormatter.java`.

1. Su `Formatter` e `format()` dovrebbero comparire **riferimenti** e **implementazioni**
   che JDT LS restituisce da `textDocument/codeLens` (+ `codeLens/resolve` ove necessario).
2. Clicca sui link di riferimenti: si deve aprire la finestra di ricerca esistente.
3. Clicca sui link di implementazioni: la ricerca deve trovare `UpperFormatter` e
   `LowerFormatter` (dipende anche dall'indicizzazione di JDT LS).
4. Modifica `UpperFormatter`, salva, controlla che i CodeLens vengano aggiornati.
5. Cambia tema e focus: i link sono blu sottolineati, senza etichette nere illeggibili.

**Pack E1:** CodeLens occupa una **riga separata sopra** la dichiarazione
(InterLineAdornmentTag), senza inserire testo nel file Java. Le categorie
native restano autorevoli; per le interfacce senza CodeLens nativo è
previsto un fallback semantico con limiti di ricerca. Dopo una modifica
locale, attendere alcuni secondi; dopo l'indicizzazione o un refactoring
multi-file, controllare che i conteggi si aggiornino.

Compilazione: `javac -d out src/main/java/lenscheck/*.java` e
`java -cp out lenscheck.CodeLensMain`.
