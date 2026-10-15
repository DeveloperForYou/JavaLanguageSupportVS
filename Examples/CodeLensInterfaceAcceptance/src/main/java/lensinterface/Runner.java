package lensinterface;

public final class Runner {
    private Runner() { }

    public static void main(String[] args) {
        Formatter upper = new UpperFormatter();
        Formatter lower = new LowerFormatter();
        if (!"HELLO".equals(upper.format("hello"))) {
            throw new AssertionError("UpperFormatter failed");
        }
        if (!"hello".equals(lower.format("HELLO"))) {
            throw new AssertionError("LowerFormatter failed");
        }
        System.out.println("INTERFACE_CODELENS_OK");
    }
}
