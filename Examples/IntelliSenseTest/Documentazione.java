/**
 * Utilita' Java locale: verifica che la Javadoc arrivi al client LSP.
 * Una Javadoc scritta nel sorgente NON richiede download di sorgenti esterni.
 */
public final class Documentazione {

    /**
     * Restituisce la concatenazione di due stringhe.
     * @param prima la prima stringa
     * @param seconda la seconda stringa
     * @return la concatenazione di prima e seconda
     */
    public static String concatena(String prima, String seconda) {
        return prima + seconda;
    }
}
