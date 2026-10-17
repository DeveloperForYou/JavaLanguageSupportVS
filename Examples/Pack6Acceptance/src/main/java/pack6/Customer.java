package pack6;

// GENERATE CONSTRUCTOR / GETTERS-SETTERS: place the cursor on this class
// or on a field. Do not add handwritten accessors until after the test.
public class Customer {
    private String name;
    private int loyaltyPoints;

    public void addPoints(int count) {
        loyaltyPoints += count;
    }
}
