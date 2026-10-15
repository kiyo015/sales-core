namespace SalesCore.Domain.Tests;

/// <summary>
/// 得意先。規則の定義元は docs/domain/business-rules.md の「4. 請求」(締め日は得意先ごとに選ぶ、出荷先と請求先は別)。
/// </summary>
public class CustomerTests
{
    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(20)]
    [InlineData(25)]
    [InlineData(31)] // 月末
    public void 締め日は5日_10日_15日_20日_25日_月末から選ぶ(int closingDay)
    {
        var customer = new Customer("C001", "本社", closingDay);

        Assert.Equal(closingDay, customer.ClosingDay);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(28)] // 月末のつもりでも、月末は 31 で表す
    [InlineData(30)]
    [InlineData(32)]
    public void それ以外の締め日は選べない(int closingDay)
    {
        Assert.Throws<ArgumentException>(() => new Customer("C001", "本社", closingDay));
    }

    [Theory]
    [InlineData("", "本社")]
    [InlineData(" ", "本社")]
    [InlineData("C001", "")]
    public void コードと名前は空にできない(string code, string name)
    {
        Assert.Throws<ArgumentException>(() => new Customer(code, name, 31));
    }

    [Fact]
    public void 請求先を指定しなければ_自分が請求先()
    {
        var customer = new Customer("C001", "本社", 31);

        Assert.Same(customer, customer.BillingTarget);
    }

    // 支店に出荷し、本社に請求する
    [Fact]
    public void 請求先を指定すれば_請求書はその得意先に送る()
    {
        var headOffice = new Customer("C001", "本社", 31);
        var branch = new Customer("C002", "大阪支店", 31, billingCustomer: headOffice);

        Assert.Same(headOffice, branch.BillingTarget);
    }
}
