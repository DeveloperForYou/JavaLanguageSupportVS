package hintcheck;

import java.util.ArrayList;

/** Pack E2: diamond inference must display ArrayList<Object>. */
public final class DiamondVarHintsDemo {
    public static void main(String[] args) {
        var obj = new ArrayList<>();
        final var other = new ArrayList<>();
        var qualified = new java.util.ArrayList<>();
        var typed = new ArrayList<String>();
        // Must not be treated as executable Java:
        String literal = "var fake = new ArrayList<>();";
        // var ignored = new ArrayList<>();
        obj.add("ok");
        other.add(7);
        qualified.add(true);
        typed.add("value");
        System.out.println(obj.size() + other.size() + qualified.size()
            + typed.size() + literal.length());
    }
}
