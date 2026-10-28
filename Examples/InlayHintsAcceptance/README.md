# Inlay Hints — test editor Java

Apri la cartella come workspace **Gradle** in Visual Studio 2026.
Apri `src/main/java/hintcheck/ParameterDemo.java`.

1. Davanti a `"Ada"`, `"Lovelace"`, `2` nella chiamata `greeting(...)` dovrebbero
   apparire le etichette parametri fornite da **JDT LS** (non testo inserito nel file).
2. Verifica gli hint di `sum(13, 29)`.
3. Scrolla la pagina, modifica i valori e salva: nessun testo hint deve essere salvato.
4. Cambia tema chiaro/scuro SENZA riaprire il file: le etichette devono rimanere leggibili.
5. Apri `VarHintsDemo.java`: controlla gli hint di tipo `Customer` per le variabili
   `customer` e `other`, che usano un costruttore esplicito.
6. Le etichette parametri di `greeting`/`sum` devono mostrare i nomi senza
   duplicati o doppio `:`; gli hint semantici restano prioritari.
7. Modifica ripetutamente una chiamata e scorri: le etichette devono
   seguire la posizione del codice senza restare ferme su righe obsolete.
8. Durante l'indicizzazione, mantieni la scheda aperta: gli hint possono
   apparire dopo una risposta LSP valida, senza riaprire il documento.
9. Verifica che l'estensione non inserisca testo nel sorgente e che
   Ctrl+Z / Ctrl+Y funzionino come prima.

Il supporto varia in base alla versione/configurazione di JDT LS. Se la risposta
`textDocument/inlayHint` è vuota, non vengono visualizzati hint inventati.

Compilazione: `javac -d out src/main/java/hintcheck/*.java` e
`java -cp out hintcheck.ParameterDemo`.
