package advancednavigation;

/** Base class for the Circle and Rectangle implementations. */
public abstract class AbstractShape implements Shape {
    private final String name;

    protected AbstractShape(String name) {
        this.name = name;
    }

    @Override
    public String render() {
        return name + " area=" + area();
    }
}
