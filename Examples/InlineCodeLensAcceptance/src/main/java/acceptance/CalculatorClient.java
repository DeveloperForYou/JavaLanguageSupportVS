package acceptance;


import java.util.Arrays;
import java.util.List;

public class CalculatorClient {
    public static void main(String[] args) {
        Calculator calculator = new Calculator();
        System.out.println(calculator.calculate(10));
        System.out.println(calculator.calculateLong(10));
        List<Integer> numbers = Arrays.asList(1, 2, 3, 4, 5);
        int sum = numbers.stream().mapToInt(calculator::calculate).sum();
       
    }
}
