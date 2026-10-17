package refactoring;

public class InvoiceCalculator {

    public double total(double unitPrice, int quantity) {
        // EXTRACT VARIABLE: select "unitPrice * quantity".
        double subtotal = unitPrice * quantity;
        // EXTRACT CONSTANT: select "0.10" (literal is a constant expression).
        double discount = Math.min(subtotal * 0.10, 20.0);
        return subtotal - discount;
    }

    public String receipt(String clientName, double total) {
        // EXTRACT METHOD: select the next THREE executable statements.
        String cleanedName = clientName.trim();
        String caption = cleanedName.toUpperCase();
        String result = caption + " paid " + total;
        return result;
    }

    public void inlineDemo() {
        // INLINE VARIABLE: place caret on "message" at println below.
        String message = "Ready".toUpperCase();
        System.out.println(message);
    }
}
