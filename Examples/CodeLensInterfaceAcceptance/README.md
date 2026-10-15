# CodeLens interfaces — acceptance test

1. In the Visual Studio 2026 **experimental** instance, choose **File > Open > Folder** and open `Examples/CodeLensInterfaceAcceptance` (the folder containing `pom.xml`). Wait until JDT LS finishes importing the Maven project.
2. Open `src/main/java/lensinterface/Formatter.java`. Verify that **above** `public interface Formatter` you see clickable **N riferimenti** and **M implementazioni**, even if JDT LS omitted native CodeLens for the interface.
3. Click **implementazioni**. Verify that navigation reaches both `UpperFormatter.java` and `LowerFormatter.java` (counts depend on the JDT LS version and how it treats implementations of interface methods).
4. Click **riferimenti** and verify the search results include references in `Runner.java` and the implementing classes.
5. Open `UpperFormatter.java` to ensure existing class/method CodeLens still works and its position is still **above** the declaration, not beside the code.
6. Verify theme light/dark and focus changes. Update a file and wait for reindexing to confirm the counters refresh.
7. For `UnusedContract.java`, JDT LS can return zero results (or `null` if the query is unavailable). This test distinguishes legitimate empty results from a failure.

**Note:** this pack does not include Visual Studio binaries and does not test the VSIX; it tests the Java fixture with JDK 21 or newer.
