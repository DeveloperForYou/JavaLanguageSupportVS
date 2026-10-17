# Native References Acceptance — VS Exp / JDT LS

Aprire con **File → Apri → Cartella** questa directory e attendere il completamento
reale dell'indicizzazione JDT LS.

1. In `Calculator.java`, cursore sulla dichiarazione `add` → **Maiusc+F12**.
   Atteso: **finestra nativa Find All References** (dockable), NON `Java — Trova tutti i riferimenti` WPF.
2. Attese almeno 5 posizioni (dichiarazione + chiamata `doubleAdd` + 2 chiamate
   `Consumer` + chiamata `Runner`).
3. Selezionare una riga nella tabella → salto file/riga corretto; testare
   ricerca nella tabella, raggruppamento per file e F8 / Maiusc+F8.
4. Modificare (senza salvare) una riga con `add` nel documento aperto e ripetere
   la ricerca: l'anteprima deve seguire lo snapshot VS, non il disco.
5. Tornare in `Calculator.java`, cliccare su **CodeLens "riferimenti"**:
   si deve aprire la **stessa finestra nativa**.
6. In Java: tasto destro e **Anteprima definizione** del menu nativo (o Alt+F12)
   continuano a funzionare, senza comando `Java: Anteprima definizione` duplicato.
7. Aprire un file C#: la navigazione e la ricerca riferimenti C# restano inalterate.
8. Verificare che **Ctrl+.**, **Ctrl+K Ctrl+D**, **Ctrl+K Ctrl+F** funzionino
   in Java, perché sono state rimosse le corrispondenti voci Java in Strumenti.

Per compilare l'esempio (indipendente dalla VSIX):

```powershell
mvn -q -DskipTests package
```

La presenza dei risultati dipende dal server JDT LS, dal classpath e dall'importazione
Maven; l'apertura della finestra nativa e' gestita da Visual Studio.
