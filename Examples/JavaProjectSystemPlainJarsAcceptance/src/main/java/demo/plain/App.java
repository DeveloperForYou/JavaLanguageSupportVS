package demo.plain;

import demo.utils.Greeting;
import org.json.JSONObject;

import com.google.common.collect.ImmutableList;
import java.util.List;


public class App {
    public static void main(String[] args) {
        System.out.println(Greeting.message());
        JSONObject jsonObject = new JSONObject();
        jsonObject.put("message", Greeting.message());
        List<String> list = ImmutableList.of("Hello", "World");
    }
}
