package pack6;

public class SimpleFormatter implements Formatter {
    @Override
    public String format(Invoice invoice) {
        return invoice.description();
    }
}
