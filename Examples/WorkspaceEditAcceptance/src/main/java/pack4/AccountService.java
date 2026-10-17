package pack4;

public final class AccountService {
    public String display(Account account) {
        return "Owner: " + account.owner();
    }
}
