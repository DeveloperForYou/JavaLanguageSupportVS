package lenscheck;

public abstract class AbstractFormatter implements Formatter {
    protected String safe(String input) { return input == null ? "" : input; }
}
