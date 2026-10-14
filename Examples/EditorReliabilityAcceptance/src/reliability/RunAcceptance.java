package reliability;

public final class RunAcceptance {
    public static void main(String[] args) {
        Formatter first = new UpperFormatter();
        AbstractFormatter second = new LowerFormatter();
        if (!"HI".equals(first.format("hi"))) throw new AssertionError("UpperFormatter");
        if (!"hi".equals(second.format("HI"))) throw new AssertionError("LowerFormatter");
        FinalGreeting greeting = new FinalGreeting();
        if (!"Hello Java".equals(greeting.greet("Java"))) throw new AssertionError("Greeting");
        System.out.println("Editor Reliability PASS");
    }
}
