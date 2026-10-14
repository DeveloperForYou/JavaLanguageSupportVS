package navux;

/** Implementation 3: test that three implementation names can be read. */
public final class TitleFormatter implements Formatter {
    @Override
    public String format(String value) {
        if (value.isEmpty()) return value;
        return value.substring(0, 1).toUpperCase(java.util.Locale.ROOT)
            + value.substring(1).toLowerCase(java.util.Locale.ROOT);
    }
}
