package reliability;

public final class LowerFormatter extends AbstractFormatter {
    @Override
    public String format(String input) { return input.toLowerCase(); }
}
