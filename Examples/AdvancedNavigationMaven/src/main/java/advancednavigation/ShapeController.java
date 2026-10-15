package advancednavigation;

import java.util.Arrays;
import java.util.List;

public final class ShapeController {
    /** Incoming: App.main and BatchRunner.runBatch. */
    public double calculate() {
        List<Shape> shapes = Arrays.asList(new Circle(2.0), new Rectangle(3.0, 4.0));
        return ShapeMath.sumAreas(shapes);
    }

    /** Incoming: App.main. Outgoing: calculate(), ShapeMath.describe(). */
    public double run() {
        double total = calculate();
        Shape shape = new Circle(1.0);
        if (ShapeMath.describe(shape).isEmpty()) {
            throw new IllegalStateException("Expected description");
        }
        return total;
    }
}
