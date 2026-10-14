package navux;

/** Implementation 1: must appear by name in the navigation chooser. */
public final class UpperFormatter implements Formatter {
    @Override
    public String format(String value) {
        return value.toUpperCase(java.util.Locale.ROOT);
    }
}
