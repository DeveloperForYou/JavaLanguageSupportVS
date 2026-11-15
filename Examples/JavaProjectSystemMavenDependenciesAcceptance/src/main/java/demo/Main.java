package demo;

import com.fasterxml.jackson.databind.ObjectMapper;

public final class Main {
    public static void main(String[] args) throws Exception {
        System.out.println(new ObjectMapper().writeValueAsString("CPS F4.B.1"));
    }
}
