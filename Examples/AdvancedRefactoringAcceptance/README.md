# Pack 5 — Test in Visual Studio 2026

Aprire **solo** `Examples/AdvancedRefactoringAcceptance` con **File > Apri > Cartella** e attendere l'importazione Maven da parte di JDT LS. Aprire `InvoiceCalculator.java`.

Test manuali (da verificare uno alla volta, annullando ogni modifica con Ctrl+Z o ripristinando il file tra i test):

1. **Estrai variabile**: selezionare soltanto `unitPrice * quantity`, tasto destro > **Java: Estrai variabile**. In alternativa selezionare e usare **Ctrl+.**.
2. **Estrai costante**: selezionare `0.10` nel calcolo dello sconto e chiedere **Java: Estrai costante**.
3. **Estrai metodo**: selezionare le tre istruzioni `String cleanedName ...`, `String caption ...`, `String result ...` nel metodo `receipt`, e scegliere **Java: Estrai metodo**. Se JDT LS rifiuta la selezione, provare un blocco senza variabili catturate.
4. **Inline variabile**: posizionare il cursore su `message` dentro `System.out.println(message);` e scegliere **Java: Inline variabile**.
5. **Controllo Pack 4**: applicare un refactoring che coinvolga anche `InvoiceClient.java` e verificare la finestra **Java: Anteprima modifiche multi-file**.
6. Verificare che la rinomina delle classi/file `.java` e la navigazione funzionino ancora; i loro componenti non vengono modificati dal Pack 5.

**Importante:** JDT LS decide quali refactoring sono disponibili e come nominare i nuovi simboli. Una selezione non valida, una versione server senza supporto o una Code Action solo lato server possono non offrire una modifica automatica. In tal caso l'estensione mostra una spiegazione e non modifica il codice arbitrariamente.

Compilazione del sorgente di partenza: `javac -d out src/main/java/refactoring/*.java`. Esecuzione: `java -cp out refactoring.InvoiceClient`.
