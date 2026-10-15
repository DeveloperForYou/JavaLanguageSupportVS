package lensinterface;

public final class LowerFormatter implements Formatter {
    @Override
    public String format(String value) {
        return value.toLowerCase();
    }
}
