package syncdemo;

public final class TestMain {
    public static void main(String[] args) {
        Processor a = new UpperProcessor();
        Processor b = new LowerProcessor();
        System.out.println(a.transform(RenameTarget.greeting()));
        System.out.println(b.transform("HELLO"));
    }
}
