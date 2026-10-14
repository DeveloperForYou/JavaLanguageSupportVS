package featurecheck;

/** Try Find All References, F12, and rename Person -> Customer (file rename). */
public final class Person {
    private final String name;

    public Person(String name) {
        this.name = name;
    }

    public String greet() {
        return "Hello, " + name;
    }
}
