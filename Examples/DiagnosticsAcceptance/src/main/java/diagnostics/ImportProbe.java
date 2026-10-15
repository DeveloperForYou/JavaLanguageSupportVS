package diagnostics;

import java.util.List;
// ATTENZIONE: manca volutamente import java.util.stream.Collectors;

public class ImportProbe {
    public static List<String> check(List<String> values) {
        return values.stream().filter(v -> !v.isEmpty())
            .collect(Collectors.toList()); // errore atteso: Collectors cannot be resolved
    }
}
