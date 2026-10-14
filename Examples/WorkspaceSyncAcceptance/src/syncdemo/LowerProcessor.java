package syncdemo;

public final class LowerProcessor extends AbstractProcessor {
    @Override public String transform(String input) { return input.toLowerCase(); }
}
