package peek;

public class CalculatorClient {
    public static void main(String[] args) {
        Calculation calculation = new Calculator();
        int result = calculation.compute(21);
        String name = new Calculator().description().toUpperCase();
        if (result != 42 || !"CALCULATED".equals(name)) {
            throw new IllegalStateException("Unexpected result");
        }
        System.out.println(name + ": " + result);
    }
}
