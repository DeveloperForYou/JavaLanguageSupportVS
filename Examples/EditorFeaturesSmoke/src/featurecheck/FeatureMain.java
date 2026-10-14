package featurecheck;

import java.util.List;

/**
 * Java editor acceptance test entry point.
 *
 * F12 on Person, greet, Formatter, format, List, String.
 * Shift+F12 on Person or format.
 * Go to Implementation on Formatter.format.
 * Go to Type Definition on firstFormatter or person.
 */
public final class FeatureMain {
    private FeatureMain() { }

    public static void main(String[] args) {
        Person person = new Person("Java Editor");
        Formatter firstFormatter = new UpperFormatter();
        Formatter secondFormatter = new LowerFormatter();
        String greeting = person.greet();
        String upper = firstFormatter.format(greeting);
        String lower = secondFormatter.format(greeting);

        List<String> greetings = List.of(upper, lower);
        if (!greetings.get(0).equals("HELLO, JAVA EDITOR") ||
            !greetings.get(1).equals("hello, java editor")) {
            throw new AssertionError("Incorrect Java acceptance project result: " + greetings);
        }
        System.out.println("PASS: " + greetings);
    }
}
