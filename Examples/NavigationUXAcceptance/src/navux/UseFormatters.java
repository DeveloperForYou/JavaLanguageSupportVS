package navux;

/**
 * Right click on Formatter -> native 'Vai all'implementazione'.
 * Right click on format() -> native 'Trova tutti i riferimenti'.
 * Confirm that clicking another window or the editor never hides node text.
 */
public final class UseFormatters {
    private UseFormatters() { }

    public static void main(String[] args) {
        Formatter first = new UpperFormatter();
        Formatter second = new LowerFormatter();
        Formatter third = new TitleFormatter();
        String value = "JaVa";
        if (!"JAVA".equals(first.format(value))) throw new AssertionError("upper");
        if (!"java".equals(second.format(value))) throw new AssertionError("lower");
        if (!"Java".equals(third.format(value))) throw new AssertionError("title");
        System.out.println("Navigation UX test PASS");
    }
}
