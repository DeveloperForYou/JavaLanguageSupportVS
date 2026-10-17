package navigation;

public class CalculatorClient {
    public static void main(String[] args) {
        Calculator calculator = new Calculator(4);
        calculator.calculate(6);
        System.out.println(calculator.getTotal());
        System.out.println(calculator.doubleTotal());
    }
}
