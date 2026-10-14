package lenscheck;

public final class LowerFormatter extends AbstractFormatter {
    @Override
    public String format(String input) { return safe(input).toLowerCase(); }
}
