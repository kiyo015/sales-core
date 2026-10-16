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

    // 締め日 31 は月末。2月は 28日(うるう年は 29日)、4月は 30日
    [Theory]
    [InlineData(31, 2026, 2, "2026-02-28")]
    [InlineData(31, 2028, 2, "2028-02-29")]
    [InlineData(31, 2026, 4, "2026-04-30")]
    [InlineData(31, 2026, 10, "2026-10-31")]
    [InlineData(25, 2026, 2, "2026-02-25")]
    [InlineData(5, 2026, 10, "2026-10-05")]
    public void その月の締め日(int closingDay, int year, int month, string expected)
    {
        var customer = new Customer("C001", "本社", closingDay);

        Assert.Equal(DateOnly.Parse(expected), customer.ClosingDateIn(year, month));
    }

    [Theory]
    [InlineData(31, "2026-10-31", true)]
    [InlineData(31, "2026-10-30", false)]
    [InlineData(31, "2026-02-28", true)]
    [InlineData(25, "2026-10-25", true)]
    [InlineData(25, "2026-10-31", false)]
    public void 締め日かどうか(int closingDay, string date, bool expected)
    {
        Assert.Equal(expected, new Customer("C001", "本社", closingDay).IsClosingDate(DateOnly.Parse(date)));
    }

    // 締め日の翌日から次の締め日までが1回の請求の期間。前回の締め日は前の月の締め日
    [Theory]
    [InlineData(31, "2026-03-31", "2026-02-28")]
    [InlineData(31, "2026-01-31", "2025-12-31")]
    [InlineData(25, "2026-10-25", "2026-09-25")]
    public void 前回の締め日は前の月の締め日(int closingDay, string closingDate, string expected)
    {
        Assert.Equal(DateOnly.Parse(expected), new Customer("C001", "本社", closingDay).PreviousClosingDate(DateOnly.Parse(closingDate)));
    }
}
