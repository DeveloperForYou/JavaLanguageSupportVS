# Semantic Tokens — test editor Java

Apri **questa cartella** con File > Apri > Cartella in Visual Studio 2026.
Apri `src/semanticcheck/Shapes.java` e attendi lo stato JDT LS inizializzato.

1. Osserva `Shapes`, `Printable`, `Kind`, `Dimension`: semantic type.
2. Osserva `describe`, `size`, `trim`, `width`: semantic method.
3. Osserva `prefix`, `names`, `local`: semantic identifier.
4. Modifica il nome della variabile `local`: il colore deve riallinearsi senza riaprire il documento.
5. Verifica tema chiaro e scuro; i colori sono i **colori nativi delle classificazioni** in VS.

Se nessuna classificazione appare, apri **Strumenti > Java: Stato IntelliSense** e verifica se
JDT LS ha annunciato `semanticTokensProvider` in `initialize`. Senza legend negoziata
il componente lascia la sola colorazione TextMate, senza inventare token.

Compilazione: `javac -d out src/semanticcheck/*.java` (PowerShell) e `java -cp out semanticcheck.Shapes`.
