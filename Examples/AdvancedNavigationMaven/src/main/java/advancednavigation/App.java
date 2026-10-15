package advancednavigation;

public final class App {
    public static void main(String[] args) {
        ShapeController controller = new ShapeController();
        double total = controller.run();
        double batch = new BatchRunner().runBatch();
        double expected = Math.PI * 4.0 + 12.0;
        if (Math.abs(total - expected) > 0.00001 || Math.abs(batch - expected) > 0.00001) {
            throw new IllegalStateException("Area mismatch");
        }
        System.out.println("NAVIGATION_TEST_OK");
    }
}
