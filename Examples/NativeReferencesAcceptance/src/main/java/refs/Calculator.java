package refs;

public final class Calculator {
    public int add(int left, int right) {
        return left + right;
    }

    public int doubleAdd(int value) {
        return add(value, value); // reference in same file
    }
}
