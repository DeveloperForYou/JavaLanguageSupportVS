package editorcheck;

import java.util.Arrays;
import java.util.List;

public class EditorSmoke {
    public static void main(String[] args) {
        SymbolTarget target = new SymbolTarget();
        List<String> names = Arrays.asList("Ada", "Linus");
        System.out.println(target.join(names)); // Hover, F12, Shift+F12
        System.out.println(target.greet("Ada")); // Signature Help: overload 1
        System.out.println(target.greet(42));    // Signature Help: overload 2
    }
}
