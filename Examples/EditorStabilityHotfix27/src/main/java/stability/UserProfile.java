package stability;

public class UserProfile {
    private final String id;
    private String name;
    private int score;

    public UserProfile(String id) {
        this.id = id;
    }

    public String getName() {
        return name;
    }

    // Generate getter/setter here:
    // id: getter only (final); name: setter only; score: getter and setter.
}
