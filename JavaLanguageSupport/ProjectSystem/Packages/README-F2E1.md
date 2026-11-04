# Java CPS F2E.1 — Riconoscimento delle source root

## Scopo

`JavaSourceRootResolver.cs` e' il primo componente di `ProjectSystem/Packages`.
E' un servizio **interno, senza effetti collaterali**, progettato per essere
riutilizzato dai Pack F2E.2 (calcolo del package) e F2F (wizard dei template).
**Questa fase non modifica il flusso nativo "Aggiungi nuovo elemento".**

### API (namespace `JavaLanguageSupport.ProjectSystem.Packages`)

- `Discover(projectDirectory, buildSystemHint, additionalRoots)` restituisce
  le radici sorgenti con percorso assoluto e relativo, tipo, origine e presenza
  fisica. Include le radici convenzionali anche se ancora da creare.
- `TryFindRootForPath(projectDirectory, fileOrDirectoryPath, out root, ...)`
  individua la radice **piu' specifica** che contiene il percorso senza usare
  prefissi ambigui (`src/main/java2` non coincide con `src/main/java`).
- `DetectBuildSystem(projectDirectory, buildSystemHint)` distingue Maven,
  Gradle e progetti semplici. Il hint opzionale sovrascrive l'autodetect.

| Build system | Radici individuate |
| --- | --- |
| Maven | `src/main/java`, `src/test/java`, oppure i valori diretti di `pom.xml` in `<build><sourceDirectory>` / `<testSourceDirectory>` |
| Gradle | `src/main/java`, `src/test/java`, piu' source set esistenti `src/<nome>/java` |
| Plain | `src` e, se presenti, `src/main/java` / `src/test/java` |
| Tutti | Radici aggiuntive esplicitamente passate in `additionalRoots` |

Le radici dichiarate esplicitamente che escono dalla directory del progetto
vengono ignorate: la fase iniziale non gestisce sorgenti condivisi esterni.
Non eseguiamo Maven o Gradle per analizzare il progetto e non eseguiamo script
arbitrari di `build.gradle` / `build.gradle.kts`.

### Limiti intenzionali di F2E.1

- Non interpreta completamente la DSL di `sourceSets` di Gradle; un percorso
  personalizzato come `sourceSets.main.java.srcDirs = ['generated']` va passato
  dal chiamante in `additionalRoots` fino all'integrazione con il modello Gradle.
- Legge le directory Maven definite direttamente in `pom.xml`; non calcola il
  modello Maven effettivo con inheritance, profile o plugin, e non valuta
  espressioni `${...}` diverse da `${project.basedir}` e `${basedir}`.
- Non calcola ancora il nome del package Java e non scrive la direttiva `package`.
- Non e' ancora connesso ai dialoghi di Visual Studio: F2E.2 e F2F lo useranno.
- I source set Gradle vengono proposti se la cartella `src/<nome>/java` esiste;
  non vengono creati automaticamente.

### Esempio d'uso da codice C# 7.3

```csharp
using JavaLanguageSupport.ProjectSystem.Packages;

var roots = JavaSourceRootResolver.Discover(projectDirectory);
JavaSourceRootInfo selectedRoot;
if (JavaSourceRootResolver.TryFindRootForPath(
    projectDirectory, absoluteFilePath, out selectedRoot))
{
    // selectedRoot.RelativePath, selectedRoot.Kind, selectedRoot.Exists
    // F2E.2: a partire da selectedRoot si calcolera' il package Java.
}
```

### Collaudo

Dopo l'applicazione del pacchetto, dalla directory dello script:

```powershell
.\Test-JavaCpsF2E1.ps1 -Repo 'C:\Users\alber\Documents\Progetti\JavaLanguageSupportVS'
```

Il test compila **solo il resolver** in un processo Windows PowerShell 5.1 e
crea progetti temporanei Maven, Gradle Groovy, Gradle Kotlin DSL e plain.
Non avvia Visual Studio, non modifica il repository e rimuove i test alla fine.
Successivamente compilare `JavaLanguageSupport` (Debug/Any CPU) e verificare
che non ci siano regressioni sul progetto `HelloJavaF1.javaproj`.

Conservare CRLF sui file `.cs` e `.ps1`. Nessun file preesistente del repo viene
sovrascritto dallo script di installazione.
