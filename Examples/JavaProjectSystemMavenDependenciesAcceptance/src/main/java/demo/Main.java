package demo;

import org.json.JSONObject;

import com.fasterxml.jackson.databind.ObjectMapper;

public final class Main {
    public static void main(String[] args) throws Exception {
        System.out.println(new ObjectMapper().writeValueAsString("CPS F4.B.1"));
        JSONObject jsonObject = new JSONObject();   
        jsonObject.put("name", "CPS F4.B.1");
        System.out.println(jsonObject.toString());
    }
}
