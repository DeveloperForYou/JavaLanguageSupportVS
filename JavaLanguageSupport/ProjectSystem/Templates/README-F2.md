# Java CPS — Pack F2A (template elementi nativi)

Questa implementazione aggiunge **sette** modelli `.vstemplate` al VSIX esistente,
senza introdurre un progetto C# aggiuntivo o modificare JDT LS, editor, CPS F1 o i comandi.

## Architettura

- `Sources/<Nome>/`: sorgenti modificabili (`.vstemplate` + `.java`).
- `ItemTemplates/Java/1033/<Nome>.zip`: archivi caricati come asset `Microsoft.VisualStudio.ItemTemplate`.
- `Build-JavaCpsItemTemplates-F2.ps1`: rigenera i ZIP dopo modifiche ai sorgenti.
- Capability di filtro: `JavaLanguageSupportCps` (via `<AppliesTo>`).
- ProjectType del modello: `Java`, coerente con `JavaProjectTypeRegistration`.

L'estensione rimane CPS. L'inclusione dei file creati nel progetto segue i glob F1
`JavaCompile Include="**\*.java"` e il provider file di Visual Studio.

## Ambito / limiti

- Modelli: classe, classe astratta, interfaccia, enum, record (JDK 16+), annotation, file vuoto.
- Nome Java derivato da `$safeitemname$` e nome file da `$fileinputname$`.
- **Non** genera ancora automaticamente la dichiarazione `package`: i parametri
  standard di Visual Studio non forniscono in modo affidabile il package Maven/Gradle
  derivato dalla cartella `src/main/java`. Il wizard contestuale e' previsto per F2B.
- **Non** include ancora il comando `Crea package Java`, distinto dalla creazione
  di una normale cartella in Esplora soluzioni (F2B).
- Non e' stato eseguito un collaudo runtime Visual Studio 2026 in questo ambiente.

## Test

1. Applicare il bundle F2A; compilare la VSIX e avviare `Exp`.
2. Aprire `Examples/JavaProjectSystemAcceptance/HelloJavaF1.javaproj`.
3. Su una cartella sorgenti, scegliere `Aggiungi > Nuovo elemento`.
4. Verificare che compaiano i sette modelli, creare `Customer.java` come classe;
   verificare `public class Customer`, il file su disco e l'item in Solution Explorer.
5. Prova `enum`, `interface`, `record` (JDK 16+) e il tema chiaro/scuro.
6. Verificare che un progetto NON Java non mostri i modelli; se compaiono,
   verificare l'interpretazione della capability `<AppliesTo>` in VS 2026.

### Diagnostica asset

Se non compaiono, usare `Test-JavaCpsF2-Vsix.ps1` nel bundle sulla VSIX generata.
La presenza dei ZIP nella VSIX conferma la distribuzione, non l'indicizzazione
in Visual Studio. Se le risorse sono presenti, controllare che l'Asset
`Microsoft.VisualStudio.ItemTemplate` punti alla directory corretta e verificare
l'installazione della VSIX nell'istanza Experimental.
