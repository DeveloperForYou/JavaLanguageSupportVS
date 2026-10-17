package pack6;

public class Invoice {
    private String description;
    private int quantity;

    public Invoice(String description, int quantity) {
        this.description = description;
        this.quantity = quantity;
    }

    // CHANGE SIGNATURE: place the cursor on total(), then select
    // Java: Cambia firma metodo, if the installed JDT LS offers it.
    public double total(double unitPrice, double taxRate) {
        return quantity * unitPrice * (1.0 + taxRate);
    }

    public String description() {
        return description;
    }

    public String getDescription() {
        return description;
    }

    public void setDescription(String description) {
        this.description = description;
    }

    public int getQuantity() {
        return quantity;
    }

    public void setQuantity(int quantity) {
        this.quantity = quantity;
    }
}
