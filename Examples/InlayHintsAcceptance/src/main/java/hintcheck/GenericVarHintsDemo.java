package hintcheck;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;

/** Regression fixture: explicit and nested Java generic constructor hints. */
public final class GenericVarHintsDemo {
    public static void main(String[] args) {
        var list = new ArrayList<String>();
        final var other = new ArrayList<Integer>();
        var nested = new HashMap<String, List<Integer>>();
        var plain = new StringBuilder();
        var diamond = new ArrayList<>();

        list.add("Hello");
        other.add(123);
        nested.put("key", other);
        plain.append(list.get(0));
        System.out.println(plain.toString() + nested.size() + diamond.size());

        // var ignored = new ArrayList<String>();
        String literal = "var ignored = new ArrayList<String>();";
        System.out.println(literal);
    }
}
