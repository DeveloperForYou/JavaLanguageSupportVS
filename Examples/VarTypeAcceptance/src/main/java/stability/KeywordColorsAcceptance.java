package stability;

import java.util.ArrayList;
import java.util.List;

// Esempio per le cinque nuove classificazioni: parole chiave,
// primitivi, var, modificatori e costanti.
public final class KeywordColorsAcceptance {
    private static final boolean ENABLED = true;
    private int count = 2;

    public static void main(String[] args) {
        var names = new ArrayList<String>();
        names.add("Ada");
        int score = 2;
        boolean accepted = false;
        if (score > 0 && ENABLED) {
            accepted = true;
        } else {
            score = 0;
        }
        Object absent = null;
        System.out.println(accepted ? names : List.of() );
        System.out.println(absent == null ? "null" : absent);
    }
}
