package layoutcheck;

public class LowerFormatter implements Formatter {
    @Override
    public String format(String input, int count) {
        return input.toLowerCase().repeat(count);
    }
}
