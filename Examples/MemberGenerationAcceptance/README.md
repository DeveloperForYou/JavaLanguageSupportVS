# MemberGenerationAcceptance

Aprire direttamente questa cartella via **File > Apri > Cartella**.
Attendere che JDT LS importi il progetto Maven.

In `Child.java`: sul nome `Child` eseguire prima **Java: Genera costruttore**,
selezionando il super-costruttore `Parent(int)` e il campo `name`. Verificare
la nuova firma. Poi eseguire **Java: Override/Implement metodi** e selezionare
`label()` per generare `@Override`.

Il progetto compila prima dei test; salva dopo la generazione e ricompila.
