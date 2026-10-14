package layoutcheck;

public class UpperFormatter implements Formatter {
    @Override
    public String format(String input, int count) {
        return input.toUpperCase().repeat(count);
    }
}
