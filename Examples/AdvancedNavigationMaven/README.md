# Advanced Navigation — Maven

Aprire **questa cartella** in Visual Studio 2026 (File > Apri > Cartella),
attendere che JDT LS termini l'importazione del progetto Maven.

## Checklist manuale

1. In `App.java`, posizionarsi su `controller.run()` e selezionare **Strumenti > Java: Anteprima definizione**. La finestra mostra il sorgente di `ShapeController.run` senza cambiare il file attivo. Pulsante **Apri definizione**: apre la classe e porta il cursore sulla dichiarazione.
2. In `ShapeController.java`, posizionarsi su `calculate` (dichiarazione), quindi **Java: Gerarchia chiamate**. Espandere **Chiamanti** per trovare `run` e `BatchRunner.runBatch`; espandere **Chiamati** per trovare `ShapeMath.sumAreas`.
3. In `Shape.java`, selezionare il nome `Shape` e aprire **Java: Gerarchia tipi**: dovrebbe mostrare `Printable` tra i supertipi e `AbstractShape` tra i sottotipi (in funzione del supporto typeHierarchy della versione JDT LS).
4. Su `AbstractShape`, espandere **Sottotipi** per trovare `Circle` e `Rectangle`.
5. Su `UnusedContract`, verificare che i sottotipi risultino vuoti, non inventati.
6. Fare doppio clic su ogni nodo per aprire il sorgente nel punto pertinente.
7. Durante l'espansione, spostare il focus nell'editor e di nuovo alla finestra; il testo deve restare leggibile nel tema chiaro e scuro. La selezione usa una sottolineatura blu.
8. Aprire/chiudere la finestra ripetutamente durante l'indicizzazione: niente eccezioni o blocchi.

La gerarchia si carica **a richiesta**. Se JDT LS non supporta `prepareTypeHierarchy`,
l'estensione indica esplicitamente che mostra soltanto un elenco piatto delle implementazioni,
non una gerarchia completa. Le notifiche LSP non garantiscono che l'indice sia completo.

Build: `mvn compile` (richiede Maven) oppure lo script `Scripts/Verify-JavaAdvancedNavigationTests.py` del pacchetto (solo JDK 21).
