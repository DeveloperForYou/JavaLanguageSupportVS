# Editor Reliability Acceptance — JavaLanguageSupportVS

Open `Examples/EditorReliabilityAcceptance` using **File > Open > Folder** in the *experimental* Visual Studio 2026 instance. Wait for **Java: Stato IntelliSense** to report that JDT LS is initialized and indexing has completed. A cold-start lookup may legitimately take longer than a warm lookup.

## Test A — file rename from its own declaration

1. Copy this test folder to a disposable location (do not mutate the sole baseline).
2. Open `src/reliability/FinalGreeting.java`.
3. Place the caret on `FinalGreeting` in `public final class FinalGreeting`.
4. Use **Java: Rinomina simbolo nel progetto** (or its existing shortcut) and enter `GreetingService`.
5. Verify the source declaration and both uses in `RunAcceptance.java` become `GreetingService`.
6. Verify `FinalGreeting.java` no longer exists and `GreetingService.java` exists in the same directory.
7. Also test Shift+Enter preview: it must show the `.java` file rename and permit selectively including it.
8. Save modified documents. Close/reopen the folder if JDT LS is still showing old diagnostics.

## Test B — file rename from a reference in another file

Start from a **fresh copy** of the baseline folder. Open `RunAcceptance.java`, put the caret on `FinalGreeting` in `FinalGreeting greeting = ...` and rename it to `GreetingService`. Expected: both source and physical `.java` filename are changed. JDT LS must be indexed; if definition resolution is unavailable, the helper deliberately refuses to guess the class identity.

## Test C — implementation navigation from interface / abstract class

Start again from the original baseline. Open `Formatter.java`. On the identifier `Formatter` (NOT `interface`), invoke **Vai all'implementazione**. Repeat three times: first on cold project load, then a second time, then after opening `UpperFormatter.java`. The list should include implementations returned by JDT LS, with readable file names.

Open `AbstractFormatter.java` and invoke Go To Implementation on `AbstractFormatter` and then on the `format` method identifier. Navigate from a use of `Formatter` inside `RunAcceptance.java`. For each, record the line under the cursor and results. Results may differ by symbol: Go To Implementation on a type vs a method is not equivalent.

Use the context menu and then **F12** on a different symbol. The later F12 must refer to the CURRENT caret, not an old right-click selection.

## Build/run independent of VSIX

From PowerShell in this directory:

```powershell
New-Item -ItemType Directory -Force out | Out-Null
javac -encoding UTF-8 -d out src\reliability\*.java
java -cp out reliability.RunAcceptance
```

Expected: `Editor Reliability PASS`.

The `javac` test checks Java sources only. It does **not** validate VSIX compilation, JDT LS indexing behavior, or Visual Studio native command routing. The extension must be rebuilt in Visual Studio 2026 after applying the patch.
