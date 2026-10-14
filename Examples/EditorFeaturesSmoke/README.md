# Single-folder editor acceptance test (JDK 21)

Open this directory with **File > Open > Folder** in Visual Studio 2026, then open
`src/featurecheck/FeatureMain.java` and wait for the JDT LS indexing to finish.

Expected behaviors:

| Action | Where | Expected result |
| --- | --- | --- |
| F12 | `Person` in `new Person(...)` | Navigate to `Person.java` |
| F12 | `person.greet()` | Navigate to `Person.greet` |
| Shift+F12 | `Person` or `format` | Window listing refs per file, double-click navigates |
| Context menu > Java: Trova tutti i riferimenti | `format` | Window with multiple references |
| Go to Implementation / context menu | `Formatter` or `format` | Choose an implementation |
| Java: Vai alla definizione del tipo | `person` | Navigate to `Person` type declaration |
| Ctrl+click | `greet` | Existing implementation still navigates |
| Rename | `Person` in `Person.java` | Rename class references and file if supported |
| Shift+Enter in rename | `Person` | Existing preview still works |

The Java LS may not have an indexed project if you open **just one file**. Open this
**folder**, or prefer the Maven/Gradle test projects.

Run without Maven or Gradle (PowerShell in this directory):

```powershell
New-Item -ItemType Directory -Force .\out | Out-Null
javac --release 21 -d .\out .\src\featurecheck\*.java
java -cp .\out featurecheck.FeatureMain
```

Expected output: `PASS: [HELLO, JAVA EDITOR, hello, java editor]`.

Do not rename the project classes before checking the run command unless you
also update the expected acceptance test source (or revert the rename afterwards).
