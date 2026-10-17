# Pack 7 — Navigation & Highlights Acceptance

Aprire **questa cartella**, che contiene `pom.xml`, con **File > Apri > Cartella**.
Attendere il completamento dell'importazione Maven in JDT LS.

1. Aprire `Calculator.java` e verificare la barra sopra l'editor: tipo
   `Calculator`, membri `Calculator(int)`, `calculate`, `getTotal`, `doubleTotal`.
2. Selezionare `CalculatorClient.java` e verificare il passaggio a `CalculatorClient`.
3. Nell'editor di `Calculator.java`, spostare il caret su `total` dentro
   `calculate`: aspettarsi l'evidenziazione semantica delle letture e scritture
   di `total`, distinguendo scrittura e lettura.
4. Verificare che un metodo sovraccaricato o omonimo di un'altra classe non
   venga evidenziato per coincidenza testuale.
5. Aprire una classe e selezionare un membro nella barra: il caret deve
   spostarsi alla dichiarazione senza cambiare il documento.
6. Provare Extract Method, poi attendere circa 1 s: la barra deve riallinearsi
   al nuovo elenco simboli senza ricalcolare continuamente i riferimenti.
7. Verificare che la barra compaia SOLO nei documenti Java e che CodeLens,
   Inline Rename e Quick Actions native mantengano il comportamento precedente.

Per il test Java senza Maven: `javac -d out src/main/java/navigation/*.java`
poi `java -cp out navigation.CalculatorClient`.
