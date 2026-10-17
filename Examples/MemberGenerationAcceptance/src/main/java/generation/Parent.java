package generation;

public class Parent {
    private final int id;

    public Parent(int id) {
        this.id = id;
    }

    public int id() {
        return id;
    }

    public String label() {
        return "Parent " + id;
    }
}
