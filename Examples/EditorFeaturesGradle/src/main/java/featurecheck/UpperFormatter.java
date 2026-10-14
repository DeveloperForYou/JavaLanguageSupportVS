package featurecheck;

/** First implementation: use Find All References on Formatter. */
public final class UpperFormatter implements Formatter {
    @Override
    public String format(String value) {
        return value.toUpperCase(java.util.Locale.ROOT);
    }
}
