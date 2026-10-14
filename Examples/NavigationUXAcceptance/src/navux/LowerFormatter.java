package navux;

/** Implementation 2: must appear by name in the navigation chooser. */
public final class LowerFormatter implements Formatter {
    @Override
    public String format(String value) {
        return value.toLowerCase(java.util.Locale.ROOT);
    }
}
