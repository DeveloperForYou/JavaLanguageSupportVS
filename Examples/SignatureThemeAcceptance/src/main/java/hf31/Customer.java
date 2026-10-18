package hf31;
import java.util.List;

public class Customer {
    private final String id;
    private String name;
    private int score;
    public Customer(String id, String name) {
        this.id = id;
        this.name = name;
    }
    public int compute(int amount, String category) {
        var labels = List.of(name, category);
        return amount + labels.size();
    }
}
