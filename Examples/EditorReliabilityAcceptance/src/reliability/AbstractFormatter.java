package reliability;

/** Abstract type with a directly overridden method in two derived classes. */
public abstract class AbstractFormatter implements Formatter {
    @Override
    public abstract String format(String input);
    public String description() { return getClass().getSimpleName(); }
}
