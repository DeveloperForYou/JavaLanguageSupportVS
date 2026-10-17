# Test manuale Inline Variable + CodeLens

Apri **questa** cartella in Visual Studio sperimentale (File > Apri > Cartella), attendi JDT LS.

1. Apri Calculator.java. Posiziona il cursore proprio sulla parola `doubled` in `return doubled + 5;`.
2. Tasto destro > **Java: Inline variabile**. Risultato atteso, se proposto da JDT LS: l'espressione `input * 2` sostituisce `doubled`, e la dichiarazione locale diventa superflua.
3. Ripristina (Ctrl+Z). Prova selezionando il nome `doubled` e poi il comando.
4. Prova un Extract Method su `calculateLong`: osserva che CodeLens non appare sopra righe o metodi errati, ma si aggiorna entro circa un secondo dopo la modifica strutturale.
5. Scrivi dentro un metodo senza aggiungere righe: i CodeLens devono rimanere stabili, senza ricalcolo ad ogni carattere.
6. Verifica CodeLens sia su Calculator.java sia su CalculatorClient.java; le classi devono essere analizzate in un progetto Maven riconosciuto.
7. Se il refactoring Inline non è disponibile, leggi i titoli `JDT LS Code Actions disponibili` nella finestra Output Java e riportali: l'estensione non deve inventare trasformazioni.

L'azione `Inline` è calcolata da JDT LS; non tutte le dichiarazioni sono ammissibili.
