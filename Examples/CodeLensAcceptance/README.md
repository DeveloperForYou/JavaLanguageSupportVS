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

NOTA: questa prima implementazione mostra CodeLens **inline all'inizio della riga**,
non ancora come una riga dedicata sopra la dichiarazione. JDT LS può non produrre
CodeLens per tutti i simboli. I suoi conteggi restano quelli restituiti dal server.

Compilazione: `javac -d out src/main/java/lenscheck/*.java` e
`java -cp out lenscheck.CodeLensMain`.
