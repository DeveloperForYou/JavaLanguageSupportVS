package hintcheck;

/** E2 ArrayVar: inlay hints are UI adornments, never Java source edits. */
public final class ArrayVarHintsDemo {
    public static void main(String[] args) {
        var arr = new String[] { "var insideArray = new Customer(\"literal\");" };
        final var other = new int[] { 1, 2, 3 };
        var matrix = new String[][] { { "one" }, { "two" } };
        var braces = new String[] { "a } quoted brace", "b { quoted brace" };
        var empty = new String[] {};
        // var fake = new String[] { "not Java code" };
        String literal = "var fake = new String[] { \"not code\" };";
        System.out.println(arr[0] + other.length + matrix[0][0] + braces[0] + empty.length + literal);
    }
}
