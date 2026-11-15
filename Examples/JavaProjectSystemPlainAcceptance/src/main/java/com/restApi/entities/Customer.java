package com.restApi.entities;

import com.restApi.entities.abstractEntities.Person;

public class Customer extends Person {

	private String name;

	private int age;

	public String getName() {
		return name;
	}

	public int getAge() {
		return age;
	}

	public void setAge(int age) {
		this.age = age;
	}

	public void setName(String name) {
		System.out.println("Customer name: " + name);
		System.exit(0);
		
		this.name = name;
	}
}