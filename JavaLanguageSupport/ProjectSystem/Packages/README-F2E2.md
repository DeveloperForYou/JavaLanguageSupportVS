# Pack F2E.2 — Risoluzione dei package Java

Dipendenza: Pack **F2E.1** (`JavaSourceRootResolver.cs`). Questo Pack aggiunge
`JavaPackageResolver.cs` e non modifica il codice esistente. I file sono
compilati automaticamente dal progetto SDK-style `JavaLanguageSupport.csproj`
con C# 7.3 e .NET Framework 4.7.2.

## API interna

- `TryResolveForFile(projectDirectory, javaFilePath, out info, out error, buildSystemHint, additionalRoots)`:
  individua la root F2E.1, calcola il package del file Java (esistente o da creare)
  e controlla la validita dei segmenti. Richiede estensione `.java`.
- `TryResolveForDirectory(...)`: stessa conversione, ma per una directory.
- `TryValidatePackageName(...)`: controlla identificatori Java e parole riservate.
- `JavaPackageInfo.Name`: `com.example.service` oppure stringa vuota per il package default.
- `JavaPackageInfo.Declaration`: `package com.example.service;` oppure stringa vuota.
- `JavaPackageInfo.GetDeclarationHeader()`: header CRLF pronto da preporsi al testo del template;
  accetta anche `"\n"` per LF.
- `JavaPackageInfo.IsModuleDescriptor`: evita la dichiarazione `package` per `module-info.java`.

Esempio per `src/main/java/com/example/services/Customer.java`:

    SourceRoot: src/main/java
    Name: com.example.services
    Declaration: package com.example.services;

Il resolver non scrive i file e non e ancora collegato alla finestra nativa
"Aggiungi nuovo elemento". La generazione vera e propria avverra durante
l'integrazione del template wizard (fase F2E.3/F2F). Non e possibile dedurre
source root arbitrarie da script Gradle dinamici senza una configurazione
esplicita; il resolver riutilizza la lista F2E.1.

## Vincoli e comportamento

- Non usa la radice del progetto come package se una source root non corrisponde.
- Confronta i segmenti dei percorsi, evitando confusione tra `java` e `java2`.
- Il package default non aggiunge alcuna dichiarazione.
- Rifiuta parole chiave Java, identificatori non validi e segmenti vuoti.
- Accetta identificatori Unicode comuni. La validazione non intende sostituire
  l'intera semantica del compilatore Java (ad esempio per Unicode ignorable).
- Non modifica JDT LS, editor, icone, VSIX manifest, template o .javaproj.
- I nuovi file sono creati con terminatori CRLF.

## Collaudo

Usare gli script del bundle scaricabile:

```powershell
.\Apply-JavaCpsF2E2.ps1 -Repo 'C:\Users\alber\Documents\Progetti\JavaLanguageSupportVS' -Apply
.\Test-JavaCpsF2E2.ps1 -Repo 'C:\Users\alber\Documents\Progetti\JavaLanguageSupportVS'
```

Lo script di verifica compila i resolver in memoria con Windows PowerShell 5.1
(e non richiede la ricompilazione della VSIX) e svolge test su directory
Maven, Gradle, Kotlin DSL, plain e source root personalizzate.
