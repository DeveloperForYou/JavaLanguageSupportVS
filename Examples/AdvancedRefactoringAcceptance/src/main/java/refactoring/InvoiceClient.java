package refactoring;

public class InvoiceClient {
    public static void main(String[] args) {
        InvoiceCalculator calculator = new InvoiceCalculator();
        System.out.println(calculator.total(19.90, 3));
        System.out.println(calculator.receipt(" Ada ", 29.90));
        calculator.inlineDemo();
    }
}
