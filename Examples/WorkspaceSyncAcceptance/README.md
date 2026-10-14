# Collaudo JDT LS / Explorer — Fix 11

Apri la cartella `Examples/WorkspaceSyncAcceptance` con Visual Studio 2026 (`File > Apri > Cartella`), non soltanto i file singoli. Lascia terminare l'importazione iniziale JDT LS.

1. Apri `Processor.java`. Su `transform`, richiama **Vai all'implementazione** immediatamente dopo l'avvio, poi una seconda volta a indicizzazione stabilizzata. Devono comparire `UpperProcessor` e `LowerProcessor`.
2. Su `AbstractProcessor`, verifica la navigazione alle classi derivate quando JDT LS è ancora impegnato. Le richieste ritentano solo per un intervallo limitato; non sono garantiti risultati prima dell'indicizzazione.
3. Su `Processor.transform`, esegui **Trova tutti i riferimenti**. Verifica che le chiamate di `TestMain` siano visibili, e che una risposta vuota non venga trattata come errore durante l'importazione.
4. Su `RenameTarget.java`, rinomina la classe `RenameTarget` in `GreetingTarget`. Verifica: (a) `GreetingTarget.java` esiste, (b) `RenameTarget.java` non esiste, (c) Esplora soluzioni mostra il nuovo nome senza riaprire il progetto, (d) `TestMain.java` usa il nuovo nome. Salva i buffer Java modificati.
5. Ripristina i file di test prima di ripetere il test (o crea una copia della cartella).
6. Strumenti > **Java: Stato IntelliSense**: controlla la nuova riga `Attivita' workspace JDT LS`. Senza notifiche di progresso, lo stato è indicativo, non garantito.

Test compilazione iniziale (PowerShell dalla directory `WorkspaceSyncAcceptance`):

```powershell
javac -encoding UTF-8 -d out (Get-ChildItem -Recurse -Filter *.java -Path src).FullName
java -cp out syncdemo.TestMain
```

Output atteso: `OK` e `hello`. La verifica UI richiede Visual Studio 2026 (istanza sperimentale).
