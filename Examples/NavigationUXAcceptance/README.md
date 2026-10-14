# Java Navigation UX — test manuale per Visual Studio 2026

Apri questa cartella con **File > Apri > Cartella** nell'istanza sperimentale VS.
Attendi il completamento dell'indicizzazione di JDT LS.

1. In `Formatter.java`, seleziona `Formatter` e usa **Vai all'implementazione** dal menu nativo del tasto destro. Devono comparire i nomi `UpperFormatter`, `LowerFormatter`, `TitleFormatter` (l'ordine e il numero esatto dipendono da JDT LS).
2. Verifica la finestra **Navigazione simboli Java**: con tema **scuro** e **chiaro**, il filtro, tutte le etichette e la selezione rimangono leggibili.
3. Seleziona `LowerFormatter`, clicca nel filtro, poi torna alla lista, poi passa a un'altra finestra: il nome selezionato deve rimanere leggibile e sottolineato in blu.
4. In `UseFormatters.java`, premi **MAIUSC+F12** su `Formatter` e su `format`. La finestra **Trova tutti i riferimenti** deve raggruppare i risultati per file.
5. Seleziona un nodo nell'albero dei riferimenti, passa al codice o a un'altra finestra, poi ritorna. Testo leggibile e sottolineatura blu permanente sul nodo selezionato.
6. Doppio clic su un riferimento: il file corretto deve aprirsi alla riga giusta.
7. Ridimensiona entrambe le finestre: contenuto leggibile. I due dialoghi non devono presentare il pulsante **Riduci a icona**.
8. Menu tasto destro: **Vai alla definizione**, **Vai all'implementazione**, **Trova tutti i riferimenti** devono comparire **una sola volta** nel menu contestuale. I comandi espliciti Java corrispondenti restano nel menu **Strumenti**.
9. Verifica anche **Java: Rinomina simbolo nel progetto**: volutamente lasciato disponibile perché la voce nativa "Rinomina..." non è ancora collegata con certezza a JavaInlineRename.

## Test console (separato dalla VSIX)

Da `Examples/NavigationUXAcceptance`:

```powershell
javac -encoding UTF-8 -d out src\navux\*.java
java -cp out navux.UseFormatters
```

Output atteso: `Navigation UX test PASS`. Il test console non certifica la UI Visual Studio né le risposte di JDT LS.
