package stability;
public class Customer extends Parent {
    private String name;
    private int age;
    public Customer(String name, int age) { super(); this.name = name; this.age = age; }
    public int total(int base, int factor) { return base * factor; }
    public static void main(String[] args) {
        var example = new Customer("Ada", 20);
        System.out.println(example.total(2, 3));
    }
}
