package navigation;

public class Calculator implements Calculation {
    private int total;

    public Calculator(int initial) {
        this.total = initial;
    }

    @Override
    public int calculate(int increment) {
        int result = total + increment;
        total = result;
        return result;
    }

    public int getTotal() {
        return total;
    }

    public int doubleTotal() {
        return getTotal() * 2;
    }

    private static class InnerCalculator {
        int multiply(int left, int right) {
            return left * right;
        }
    }
}
