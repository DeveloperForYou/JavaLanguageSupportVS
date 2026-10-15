package rainbow;

import java.util.Arrays;
import java.util.List;

public final class RainbowBracketsDemo {
    private RainbowBracketsDemo() { }

    public static void main(String[] args) {
        List<List<Integer>> groups =
            Arrays.asList(Arrays.asList(1, 2), Arrays.asList(3, 4));
        int result = Math.max(
            (groups.get(0).get(1)),
            Math.min(9, ((groups.get(1)).get(0)))
        );
        groups.stream().filter(
            x -> {
                return x.get(0) > 0;
            }
        ).collect(Collectors.toList());
        groups.stream().filter
        (   x -> {
                return x.get(0) > 0;
            }
        );
        // Queste parentesi ({[]}) non devono diventare arcobaleno.
        String literal = "( [ { } ] )";
        char character = '(';
        String multiline = """
            { [
                (test)
            ] }
            """;
        if ((result > 0) && (literal.length() != 0)) {
            System.out.println(multiline.trim() + " / " + character + ": " + result);
        }
    }
}
