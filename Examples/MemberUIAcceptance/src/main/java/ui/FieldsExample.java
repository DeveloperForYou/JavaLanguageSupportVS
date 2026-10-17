package ui;

import java.util.Arrays;
import java.util.List;

public class FieldsExample extends Parent {
    private final String id = "fixed";
    private String name;
    private int score;
    private List<String> names;
    private boolean active;

    public String getName() { return name; }

    public void inferredTypes() {
        var texts = Arrays.asList("a", "b");
        var count = texts.size();
        System.out.println(count + texts.size());
    }
}
