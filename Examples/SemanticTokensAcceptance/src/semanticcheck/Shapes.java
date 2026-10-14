package semanticcheck;

import java.util.ArrayList;
import java.util.List;

public final class Shapes implements Printable {
    private final List<String> names = new ArrayList<>();

    public enum Kind { CIRCLE, RECTANGLE }

    public record Dimension(int width, int height) { }

    @Override
    public String describe(String prefix) {
        String local = prefix.trim();
        names.add(local);
        return local + ": " + names.size();
    }

    public static void main(String[] args) {
        Printable shape = new Shapes();
        System.out.println(shape.describe(" shape "));
        System.out.println(Kind.CIRCLE);
        System.out.println(new Dimension(10, 20).width());
    }
}
