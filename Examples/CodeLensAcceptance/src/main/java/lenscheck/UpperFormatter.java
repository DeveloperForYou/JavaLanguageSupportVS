package lenscheck;

public final class UpperFormatter extends AbstractFormatter {
    @Override
    public String format(String input) { return safe(input).toUpperCase(); }
}
