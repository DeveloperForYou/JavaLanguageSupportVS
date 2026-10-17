package pack6;

public class Main {
    public static void main(String[] args) {
        Invoice invoice = new Invoice("Item", 3);
        double total = invoice.total(12.0, 0.22);
        System.out.println(new SimpleFormatter().format(invoice) + ": " + total);
        Customer customer = new Customer();
        customer.addPoints(5);
    }
}
