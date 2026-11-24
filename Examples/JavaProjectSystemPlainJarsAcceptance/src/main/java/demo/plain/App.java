package demo.plain;

import java.util.List;

import org.json.JSONObject;

import com.fasterxml.jackson.core.JsonProcessingException;
import com.fasterxml.jackson.databind.JsonMappingException;
import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.dataformat.yaml.YAMLFactory;
import com.google.common.collect.ImmutableList;

import demo.utils.Greeting;

public class App {
	public static void main(String[] args) {
		System.out.println(Greeting.message());
		JSONObject jsonObject = new JSONObject();
		jsonObject.put("message", Greeting.message());
		List<String> list = ImmutableList.of("Hello", "World");
		ObjectMapper mapper = new ObjectMapper(new YAMLFactory());

		String yaml = "nome: Alberto\neta: 30";

		JsonNode node;
		try {
			node = mapper.readTree(yaml);
			System.out.println(node.get("nome").asText());
		} catch (JsonMappingException e) {
		} catch (JsonProcessingException e) {
		}

	}
}
