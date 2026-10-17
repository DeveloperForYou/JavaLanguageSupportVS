package refs;

public final class Consumer {
    public int process() {
        Calculator calculator = new Calculator();
        return calculator.add(10, 20) + calculator.add(30, 40);
    }
}
