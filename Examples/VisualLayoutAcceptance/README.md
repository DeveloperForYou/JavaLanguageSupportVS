# Test visuali Pack 2 — Layout e colorazione

Apri **Examples/VisualLayoutAcceptance** con File > Apri > Cartella nell'istanza
sperimentale di Visual Studio 2026. Attendi l'import di Maven e l'indicizzazione JDT LS.

1. **CodeLens**: apri `Formatter.java`, `UpperFormatter.java` e `LayoutMain.java`.
   Le etichette dei riferimenti/implementazioni devono occupare una riga **sopra**
   la dichiarazione della classe/interfaccia o del metodo, non a sinistra del nome
   sulla stessa riga. Verifica con righe indentate e con finestra ridimensionata.
2. **Inlay Hints**: apri `LayoutMain.java`; nelle chiamate a `execute` e `format`
   i suggerimenti `formatter:`, `text:`, `repeat:` (se restituiti da JDT LS)
   devono essere **accanto ai rispettivi argomenti, sulla stessa riga**; nessuna
   modifica deve essere inserita nel file reale. Verifica anche il cambio focus.
3. **Semantic Highlighting**: in tutti i file confronta i colori di `Formatter`
   (tipo) e `execute` / `format` (metodi): **devono essere diversi**. Nel tema
   scuro, tipi = verde acqua; metodi = giallo chiaro. Nel tema chiaro,
   tipi = verde acqua scuro; metodi = marrone. Controlla parole chiave e stringhe.
4. **Modifica codice**: cambia temporaneamente `"Hi"` in `"Hello"`; verifica
   che hints e CodeLens si aggiornino senza comparire duplicati.
5. **Temi**: ripeti nei temi chiaro e scuro. Se cambi tema a editor aperto e
   i colori non si aggiornano, riavvia l'istanza sperimentale (i formati MEF
   sono creati all'avvio). Puoi personalizzare i colori da
   Strumenti > Opzioni > Ambiente > Tipi di carattere e colori:
   `Java: tipi, classi e interfacce (JDT LS)` e `Java: metodi e chiamate (JDT LS)`.

Nota: le etichette CodeLens/Inlay dipendono dalla disponibilita' delle risposte
JDT LS e dalle sue impostazioni; non vengono fabbricati conteggi artificiali.

Test di compilazione senza Maven: `javac -d out src/main/java/layoutcheck/*.java`
(la directory `out` e' ignorata da Git se il repository ha la relativa regola).
