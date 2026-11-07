# Pack F2H.1 — Rinomina semantica package Java (primo collaudo)

## Obiettivo

Aggiungere **Java: Rinomina package...** al menu contestuale dei package CPS
(`.javaproj`), senza modificare la creazione dei package e i template F2F/F2G.
La rinomina dei riferimenti Java viene determinata da **JDT LS**
(`textDocument/rename`). Nel **hotfix F2H.1a** il client completa **soltanto**
la dichiarazione `package` dei sorgenti che verranno effettivamente spostati,
se il server l'ha omessa. Non modifica mai gli `import` o gli altri riferimenti
tramite sostituzioni locali.

## Sicurezza e ambito del primo pack

- Supporta **package foglia con almeno un file `.java`**, senza sottocartelle.
- Modifica solo il segmento finale (`com.example.services` -> `com.example.business`).
- Aggiorna `import` e riferimenti Java **solo se JDT LS li restituisce** nel WorkspaceEdit.
- **F2H.1a:** dove mancano, completa le dichiarazioni `package` dei file della
  directory rinominata con corrispondenza esatta e univoca; abortisce se resta
  il vecchio nome package in un sorgente Java del progetto. La scansione
  conservativa può richiedere la rimozione manuale di riferimenti testuali
  residui nei commenti o nelle stringhe prima di riprovare.
- Sposta fisicamente la directory e aggiorna eventuali `<Folder Include="..." />`
  espliciti nel `.javaproj`. Gli altri file della directory seguono la cartella.
- Mostra prima l'elenco completo dei file modificati/spostati e il numero degli edit.
- Rifiuta URI esterni, operazioni di risorsa non riconosciute, sorgenti mancanti,
  target già esistenti, file di sola lettura e documenti VS aperti.
- Fa un backup persistente in `.vs/JavaPackageRenameBackups/` prima delle modifiche.
  `.vs/` deve rimanere esclusa dal controllo versione.
- Tenta rollback automatico in caso di errore durante scrittura/spostamento.
- **Non garantisce ancora Ctrl+Z globale o il refactoring ricorsivo dei sottopackage.**
  Si tratta di limiti espliciti del primo pack, da rimuovere prima della versione stabile.
- Non rinomina file `.java` isolati né resource roots `resources`.
- Non limita artificialmente il numero di package. La preparazione delle modifiche
  può tuttavia richiedere tempo in progetti di grandi dimensioni.

## Collaudo consigliato (su copia di progetto, con Git pulito)

1. Chiudere le schede dei sorgenti coinvolti; avviare Visual Studio Experimental.
2. Aprire `HelloJavaF1.javaproj` e attendere l'indicizzazione JDT LS.
3. Creare un package foglia, ad esempio `com.example.oldservice`, con `Service.java`.
4. Creare in un *altro* package un file che importi `Service`.
5. Tasto destro su `oldservice` -> **Java: Rinomina package...**.
6. Digitare `newservice`, verificare l'anteprima, confermare.
7. Verificare cartella, dichiarazione `package`, `import` nell'altro file,
   struttura Esplora soluzioni e, se presente, `<Folder Include="..." />`.
8. Provare **Annulla** nell'anteprima: nessuna modifica deve avvenire.
9. Provare un package con sottocartelle: deve essere rifiutato senza modifiche.
10. Provare file aperti e un package target già esistente: nessuna modifica.
11. Se il server risponde senza edit/movimenti coerenti, la rinomina viene rifiutata.

## Problemi noti e sviluppi successivi

JDT LS deve aver importato i sorgenti. Quando il client Visual Studio
non annuncia `workspace.workspaceEdit.resourceOperations`, JDT LS può omettere
nel WorkspaceEdit l'operazione di rinomina del package e, con essa, gli edit
delle dichiarazioni `package`. **F2H.1a** ripristina esclusivamente queste
dichiarazioni in modo verificato, lasciando a JDT LS tutti gli import e gli
altri riferimenti. Il Pack resta prudente: se il server non produce alcun edit
affidabile oppure un riferimento al vecchio package rimane nel codice Java,
la rinomina viene rifiutata. Non modifica riferimenti in XML, properties,
resource o stringhe di configurazione.
Il modello a dischi presuppone schede chiuse: per il Pack F2H.2 è previsto il
riutilizzo dei buffer/Undo nativi C1-C2, la gestione dei sottopackage e
l'applicazione delle rinomine più complesse con sincronizzazione CPS.

## F2H.1b — Schede Java aperte durante la rinomina

**F2H1B-LIVE-DOCUMENTS:** non è più necessario chiudere i file `.java` interessati:
per i documenti aperti e già salvati il comando aggiorna il buffer dell'editor,
salva il contenuto, sposta la directory e aggiorna il percorso nella Running Document
Table (RDT) e in `ITextDocument`. Le schede rimangono aperte.

- Se un file Java interessato ha **modifiche non salvate**, l'operazione non
  parte: salva il file e riprova; non devi chiudere la scheda.
- Se il file `.javaproj` è aperto come testo nell'editor, chiudi **solo quella**
  scheda dopo averlo salvato (il file progetto è modificato su disco).
- Controllo preliminare del contenuto e snapshot; backup in `.vs/` e ripristino
  best-effort in caso di errore, segnalando qualsiasi rollback incompleto.
- Questo hotfix mantiene le limitazioni F2H.1: package foglia, anteprima e
  controllo degli import non modificati da JDT LS. **Ctrl+Z globale resta da
  implementare**: prima del collaudo crea un commit Git. Il buffer Java può
  ancora avere proprie voci di Undo, che NON rappresentano il refactoring
  completo e non devono essere usate come ripristino del package.
- Provare prima su una copia del progetto: con `Customer.java` e un importatore
  aperti e salvati, rinominare `oldservice` in `newservice`; verificare che le
  schede restino aperte con titoli e contenuti aggiornati. Ripetere con un
  documento dirty per verificare il rifiuto senza perdita di dati.

## F2H.1c — Finestre WPF per la rinomina package

Le finestre presentano un testo orientato all'utente, privo di numerazioni dei Pack:

- **Rinomina package Java:** mostra nome attuale, ultimo segmento modificabile,
  nome completo e directory di destinazione, validazione e spiegazione del flusso.
- **Anteprima rinomina package Java:** presenta il prima/dopo, contatori distinti
  (file da spostare, file da modificare, modifiche al codice) e un elenco ad albero
  per esaminare i dettagli di ogni file, senza caselle che suggeriscano
  erroneamente la possibilità di applicare modifiche parziali.
- Resta visibile l'avviso: schede Java aperte e salvate preservate, backup
  prima della rinomina, niente Undo globale; nessun file viene modificato
  finché l'utente non conferma nell'anteprima.
- Le versioni del Pack rimangono **soltanto** nei commenti, negli script di
  manutenzione e nella documentazione tecnica; non nei titoli, nelle descrizioni,
  nei messaggi o nelle anteprime delle finestre WPF.

**Test manuali:** aprire schede Java salvate, avviare la rinomina del package,
verificare anteprima, nome nuovo, destinazione, categorie file e dettagli.
Provare anche nome invariato, segmento non valido, cartella esistente e
annullamento. La compilazione C# e l'esecuzione in VS Experimental vanno
verificate dopo l'applicazione.
