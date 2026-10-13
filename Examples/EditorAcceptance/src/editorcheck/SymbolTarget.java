package editorcheck;

import java.util.List;

/**
 * Esempio per verificare Quick Info, riferimenti e rename.
 * <p>Link Javadoc interno: {@link java.util.List}.</p>
 * <pre>{@code
 * String text = "abc";
 * System.out.println(text);
 * }</pre>
 * @see java.util.ArrayList
 */
public class SymbolTarget {
    private String message = "Hello";

    /**
     * Elabora un elenco di nomi.
     * @param names nomi da unire
     * @return testo risultante
     * @throws IllegalArgumentException se names e' null
     */
    public String join(List<String> names) {
        if (names == null) throw new IllegalArgumentException("names");
        return String.join(", ", names);
    }

    /** @param value nome da salutare @return saluto */
    public String greet(String value) { return message + ", " + value; }

    /** @param value numero da salutare @return saluto */
    public String greet(int value) { return message + ", " + value; }
}
