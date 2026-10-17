package peek;

public class Calculator implements Calculation {
    @Override
    public int compute(int base) {
        return base * 2;
    }

    public String description() {
        return "calculated";
    }
}
