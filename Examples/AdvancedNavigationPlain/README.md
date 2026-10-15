# Advanced Navigation — progetto Java senza Maven/Gradle

Aprire questa cartella con **File > Apri > Cartella** in Visual Studio 2026.
I file in `src/advancednavigation` corrispondono a quelli dell'esempio Maven.
Ripetere i test Peek Definition, Call Hierarchy e Type Hierarchy descritti nel
README di `Examples/AdvancedNavigationMaven`.

Build senza Maven: usare `Scripts/Verify-JavaAdvancedNavigationTests.py`.
Il progetto serve a verificare anche l'importazione JDT LS delle cartelle Java
senza `pom.xml` / `build.gradle`; alcune versioni del server possono richiedere
una configurazione esplicita del source path per ottenere navigazione cross-file.
