# Pack 6: prove nell'istanza sperimentale VS2026

Aprire SOLO questa cartella con File > Apri > Cartella; attendere che JDT LS abbia indicizzato il progetto Maven.

- Change Method Signature: cursore su `Invoice.total(...)`. Verificare aggiornamento di `Main.java` quando JDT LS espone il refactoring e l'operazione genera un WorkspaceEdit applicabile.
- Generate Constructor: cursore su `class Customer` (o sui campi). Controllare se JDT LS offre la Code Action.
- Generate Getter/Setter: cursore sui campi di `Customer`. Controllare la generazione.
- Override/Implement: cursore su `class ExtendedFormatter`. Provare `Override/Implement metodi` oppure `Ctrl + .`.
- Find All References su `Invoice.total` e sul chiamante in `Main.java`: prima modificare l'editor SENZA salvare, quindi verificare che le anteprime della ricerca riflettano la nuova versione dei buffer aperti. La ricerca include la dichiarazione, CodeLens normalmente no.

Le azioni dipendono dalle capabilities della versione Eclipse JDT LS. Se mancano, il comando non inventa codice: aprire View > Output > Java Build/Run/Test e leggere i titoli delle Code Actions effettivamente ricevute.

Il codice originale compila con Java 17+, senza dipendenze di test aggiuntive.
