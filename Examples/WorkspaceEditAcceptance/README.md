# Pack 4 – WorkspaceEditAcceptance

Il progetto Maven usa soltanto Java 17 e tre classi. Non contiene plugin che
richiedono un particolare debugger o Project System di Visual Studio.

## Verifiche manuali nell'istanza sperimentale Visual Studio 2026

1. Aprire la cartella `Examples/WorkspaceEditAcceptance`, attendere JDT LS.
2. Aprire `Main.java`; verificare che Ctrl+. e le Code Actions già funzionanti
   continuino ad applicare import/modifiche sul singolo documento.
3. Provare un refactoring proposto da JDT LS che restituisca modifiche in due
   file .java (quando disponibile nella versione server installata): la nuova
   finestra deve elencare entrambi i file prima di Applica tutte.
4. Annulla non modifica nessun buffer. Applica tutte lascia i buffer modificati
   ma non li salva automaticamente. Controllare entrambi in Visual Studio.
5. Se una Code Action propone rename/create/delete, aspettarsi rifiuto
   esplicito e nessuna modifica parziale (questa release NON le applica).
6. Verificare separatamente la rinomina classe/file già esistente: deve
   continuare a usare la preview dedicata.
7. Durante l'applicazione, modificare manualmente uno dei file dopo la preview:
   l'operazione deve essere rifiutata (snapshot cambiato).

Compilazione Java indipendente: `javac -d out src/main/java/pack4/*.java`.
Il progetto non dimostra autonomamente una Code Action multi-file di JDT LS,
che dipende dalla versione del server e dai refactoring esposti.
