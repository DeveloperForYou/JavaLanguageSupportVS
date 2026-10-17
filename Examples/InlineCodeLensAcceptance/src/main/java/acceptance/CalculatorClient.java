package acceptance;

public class CalculatorClient {
    public static void main(String[] args) {
        Calculator calculator = new Calculator();
        System.out.println(calculator.calculate(10));
        System.out.println(calculator.calculateLong(10));
    }
}
