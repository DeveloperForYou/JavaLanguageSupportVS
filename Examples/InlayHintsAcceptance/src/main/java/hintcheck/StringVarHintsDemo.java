package hintcheck;

/** E2 regression: Java String literals initialized with var. */
public final class StringVarHintsDemo {
    public static void main(String[] args) {
        var text = "Hello";
        final var escaped = "Contains a quote: \"yes\"";
        var path = "C:\\temp\\file";
        var empty = "";
        var fromCall = String.valueOf(123); // semantic JDT LS only
        System.out.println(text + escaped + path + empty + fromCall);
        // var fake = "not executable Java";
        String untouched = "var fake = \"inside another string\";";
        System.out.println(untouched);
    }
}
