package stability;
import java.util.ArrayList;
public class VarHints {
    public static void main(String[] args) {
        var example = new Customer("Ada", 20); // expect Customer visual inlay hint
        var plainString = "example";            // no custom fallback hint
        var items = new ArrayList<String>();    // type hint from server if offered
        System.out.println(example.getName() + plainString + items.size());
    }
}
