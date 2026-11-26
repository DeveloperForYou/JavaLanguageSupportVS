// JDTLS-NOSOURCES-ACCEPTANCE-20261126: generated probe; safe removal only if unchanged.
package demo.nosources;

import org.json.JSONObject;

public final class LibraryProbe {
    private LibraryProbe() { }

    public static String probe() {
        return new JSONObject().put("backend", "ok").getString("backend");
    }
}
