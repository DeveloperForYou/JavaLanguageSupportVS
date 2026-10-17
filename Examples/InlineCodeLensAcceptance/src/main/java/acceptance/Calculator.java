package acceptance;

public class Calculator {
    public int calculate(int input) {
        int doubled = input * 2;
        // INLINE VARIABLE: place caret on `doubled` in the next line.
        return doubled + 5;
    }

    public int calculateLong(int base) {
        // EXTRACT METHOD: select the next three Java statements.
        int third = getThird(base);
        return third;
    }

    private int getThird(int base) {
        int first = base * 2;
        int second = first + 3;
        int third = second * 2;
        return third;
    }
}
