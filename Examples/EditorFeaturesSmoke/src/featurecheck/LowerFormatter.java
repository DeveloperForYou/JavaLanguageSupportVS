package featurecheck;

/** Second implementation: Go to Implementation should offer both classes. */
public final class LowerFormatter implements Formatter {
    @Override
    public String format(String value) {
        return value.toLowerCase(java.util.Locale.ROOT);
    }
}
