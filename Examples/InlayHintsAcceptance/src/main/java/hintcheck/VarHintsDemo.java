package hintcheck;

import java.util.ArrayList;

/** Pack E2: var type hints + semantic parameter hints. */
public final class VarHintsDemo {
    private static final class Customer {
        private final String name;
        Customer(String name) { this.name = name; }
        String getName() { return name; }
    }

    public static void main(String[] args) {
		var list = new ArrayList<String>();
        var customer = new Customer("Ada");
        final var other = new Customer("Grace");
        var joined = String.join("-", customer.getName(), other.getName());
        System.out.println(joined);
        // Not code: var ignored = new Customer("comment");
        String text = "var insideString = new Customer(\"literal\");";
        System.out.println(text);
    }
}
