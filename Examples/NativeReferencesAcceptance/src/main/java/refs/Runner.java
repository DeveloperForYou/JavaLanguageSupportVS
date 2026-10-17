package refs;

public final class Runner {
    public static void main(String[] args) {
        Calculator c = new Calculator();
        System.out.println(c.add(2, 3));
        System.out.println(new Consumer().process());
        var result = c.doubleAdd(5);
        System.out.println(result);
    }
}
