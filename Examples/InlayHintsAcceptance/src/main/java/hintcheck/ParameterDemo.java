package hintcheck;

public final class ParameterDemo {
    public static String greeting(String firstName, String lastName, int repeat) {
        return (firstName + " " + lastName).repeat(repeat);
    }

    public static int sum(int left, int right) { return left + right; }

    public static void main(String[] args) {
        String result = greeting("Ada", "Lovelace", 2);
        int value = sum(13, 29);
        System.out.println(result + " = " + value);
    }
}
