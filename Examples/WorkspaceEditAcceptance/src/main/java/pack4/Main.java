package pack4;

public final class Main {
    public static void main(String[] args) {
        Account account = new Account("Ada");
        System.out.println(new AccountService().display(account));
    }
}
