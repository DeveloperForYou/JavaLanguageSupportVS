package lenscheck;

public final class CodeLensMain {
    public static void main(String[] args) {
        Formatter[] items = { new UpperFormatter(), new LowerFormatter() };
        for (Formatter item : items) System.out.println(item.format("MiXeD"));
    }
}
