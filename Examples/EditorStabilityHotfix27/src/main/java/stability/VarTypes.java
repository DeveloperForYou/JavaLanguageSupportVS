package stability;

import java.util.List;
import java.util.Arrays;

public final class VarTypes {
    private static List<String> loadNames() {
        return Arrays.asList("Ada", "Linus", "Grace");
    }

    public static void main(String[] args) {
        var names = loadNames(); // Should show inferred type : List<String> (JDT LS dependent).
        var total = names.size(); // Should show inferred type : int (JDT LS dependent).
        System.out.println(total);
    }
}
