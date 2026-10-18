package hf31;
public class Consumer {
    public static void main(String[] args) {
        Customer customer = new Customer("42", "demo");
        System.out.println(customer.compute(10, "test"));
    }
}
