package advancednavigation;

import java.util.List;

public final class ShapeMath {
    private ShapeMath() {
    }

    /** Called by calculate(), and indirectly by App.main(). */
    public static double sumAreas(List<? extends Shape> shapes) {
        double result = 0;
        for (Shape shape : shapes) {
            result += shape.area();
        }
        return result;
    }

    public static String describe(Shape shape) {
        return shape.render();
    }
}
