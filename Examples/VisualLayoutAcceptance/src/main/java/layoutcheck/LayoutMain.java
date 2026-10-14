package layoutcheck;

public final class LayoutMain {
    private LayoutMain() { }

    public static String execute(Formatter formatter, String text, int repeat) {
        return formatter.format(text, repeat);
    }

    public static void main(String[] args) {
        Formatter formatter = new UpperFormatter();
        Formatter alternative = new LowerFormatter();
        System.out.println(execute(formatter, "Hi", 2));
        System.out.println(execute(alternative, "Bye", 1));
    }
}
