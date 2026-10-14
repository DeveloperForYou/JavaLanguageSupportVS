# Inlay Hints — test editor Java

Apri la cartella come workspace **Gradle** in Visual Studio 2026.
Apri `src/main/java/hintcheck/ParameterDemo.java`.

1. Davanti a `"Ada"`, `"Lovelace"`, `2` nella chiamata `greeting(...)` dovrebbero
   apparire le etichette parametri fornite da **JDT LS** (non testo inserito nel file).
2. Verifica gli hint di `sum(13, 29)`.
3. Scrolla la pagina, modifica i valori e salva: nessun testo hint deve essere salvato.
4. Cambia tema; clicca altrove: il testo delle etichette deve rimanere leggibile.

Il supporto varia in base alla versione/configurazione di JDT LS. Se la risposta
`textDocument/inlayHint` è vuota, non vengono visualizzati hint inventati.

Compilazione: `javac -d out src/main/java/hintcheck/*.java` e
`java -cp out hintcheck.ParameterDemo`.
