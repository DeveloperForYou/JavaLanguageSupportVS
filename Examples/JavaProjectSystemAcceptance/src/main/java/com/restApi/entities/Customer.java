package com.restApi.entities;

import com.restApi.entities.abstractEntities.AbstractClass;

public class Customer extends AbstractClass{

    private String name;

    public String getName() {
        return name;
    }

    public void setName(String name) {
        this.name = name;
    }
}