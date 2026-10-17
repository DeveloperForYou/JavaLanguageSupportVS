package generation;

// TEST: cursor on Child then Java: Genera costruttore. Choose Parent(int)
// and field name. Then override label() via Java: Override/Implement metodi.
public class Child extends Parent {
    private final String name;

    public Child(int id, String name) {
        super(id);
        this.name = name;
    }

    public Child() {
        super(0);
        this.name = "default";
    }

    public String name() {
        return name;
    }

    @Override
    public String label() {
        return super.label();
    }
}
