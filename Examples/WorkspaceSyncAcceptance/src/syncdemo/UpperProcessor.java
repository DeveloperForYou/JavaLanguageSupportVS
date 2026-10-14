package syncdemo;

public final class UpperProcessor extends AbstractProcessor {
    @Override public String transform(String input) { return input.toUpperCase(); }
}
