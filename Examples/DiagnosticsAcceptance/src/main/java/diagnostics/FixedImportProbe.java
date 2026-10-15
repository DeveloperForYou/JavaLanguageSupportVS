package diagnostics;

import java.util.List;
import java.util.stream.Collectors;

public class FixedImportProbe {
    public static List<String> check(List<String> values) {
        return values.stream().filter(v -> !v.isEmpty()).collect(Collectors.toList());
    }
}
