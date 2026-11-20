namespace SalesCore.Domain.Tests;

/// <summary>
/// 入金と消込。入金は、請求先の未回収の請求書に締め日の古い順に充てる。上限は請求書の今回分(今回売上＋消費税)。
/// 規則の定義元は docs/domain/business-rules.md の「4. 請求」の「入金と消込」(2026-11-12・2026-11-20 本人の決定)。
/// 渡す請求書の順は、わざと締め日の新しい順にする(渡した順や Id の順に充てる誤りが見えるように)。
/// </summary>
public class ReceiptTests
{
    private static readonly TaxRate Standard = TaxRate.Of(10m);
    private static readonly Money Threshold = Money.Of(1_000_000m);
    private static readonly DateOnly Today = new(2030, 12, 10);
    private static readonly DateOnly ReceivedOn = new(2030, 12, 5); // 今日とずらす(取消の日付を入金日と取り違える誤りが見えるように)

    private static readonly Customer HeadOffice = new("C001", "本社", 31);
    private static readonly Customer Branch = new("C002", "大阪支店", 31, billingCustomer: HeadOffice);
    private static readonly Customer Other = new("C003", "別の会社", 31);

    // 税抜 yen の売上1件の請求書。税は10%なので、今回分は yen × 1.1
    private static Invoice InvoiceOf(Customer billing, DateOnly closingDate, decimal yen)
    {
        var line = new SalesOrderLine(1, yen, Standard);
        var order = new SalesOrder(billing, closingDate.AddDays(-20), [line], Threshold);
        order.Approve();
        var record = order.InstructShipment([new(line, 1)]).Confirm(closingDate.AddDays(-10));
        return Invoice.Create(billing, closingDate.AddDays(1 - closingDate.Day), closingDate, [record]);
    }

    private static Invoice October() => InvoiceOf(HeadOffice, new DateOnly(2030, 10, 31), 1000m); // 1,100円

    private static Invoice November() => InvoiceOf(HeadOffice, new DateOnly(2030, 11, 30), 2000m); // 2,200円

    private static Receipt Register(decimal yen, params Invoice[] invoices) =>
        Receipt.Register(HeadOffice, ReceivedOn, Money.Of(yen), invoices, Today);

    private static (Invoice Invoice, decimal Yen)[] AllocationsOf(Receipt receipt) =>
        receipt.Allocations.Select(a => (a.Invoice, a.Amount.Yen)).ToArray();

    [Fact]
    public void 請求額ちょうどの入金は_その請求書に全額を充てる()
    {
        var october = October();

        var receipt = Register(1100m, october);

        Assert.Equal([(october, 1100m)], AllocationsOf(receipt));
        Assert.Equal((Money.Of(1100m), Money.Zero), (october.AllocatedAmount, october.Outstanding));
        Assert.Equal((Money.Of(1100m), Money.Zero), (receipt.AllocatedAmount, receipt.Unallocated));
    }

    [Fact]
    public void 足りない入金は_一部だけ充て_残りは未回収のまま()
    {
        var october = October();

        var receipt = Register(600m, october);

        Assert.Equal([(october, 600m)], AllocationsOf(receipt));
        Assert.Equal(Money.Of(500m), october.Outstanding);
        Assert.Equal(Money.Zero, receipt.Unallocated);
    }

    [Fact]
    public void 複数の請求書にまたがる入金は_締め日の古い順に充てる()
    {
        var october = October();
        var november = November();

        var receipt = Register(1500m, november, october); // 渡す順は新しい順

        Assert.Equal([(october, 1100m), (november, 400m)], AllocationsOf(receipt));
        Assert.Equal((Money.Zero, Money.Of(1800m)), (october.Outstanding, november.Outstanding));
    }

    [Fact]
    public void 多い入金は_すべての請求書に充てた残りを前受として残す()
    {
        var october = October();
        var november = November();

        var receipt = Register(5000m, november, october);

        Assert.Equal([(october, 1100m), (november, 2200m)], AllocationsOf(receipt));
        Assert.Equal((Money.Of(3300m), Money.Of(1700m)), (receipt.AllocatedAmount, receipt.Unallocated));
    }

    [Fact]
    public void 未回収の請求書が無ければ_入金はすべて前受になる()
    {
        var receipt = Register(1000m);

        Assert.Empty(receipt.Allocations);
        Assert.Equal(Money.Of(1000m), receipt.Unallocated);
    }

    // 1回目の入金で10月の一部を消した。2回目は10月の残り500円から充て、消し終えた請求書には充てない
    [Fact]
    public void 一部を消した請求書は_残りの分だけ充て_消し終えた請求書は飛ばす()
    {
        var october = October();
        var november = November();
        Register(600m, november, october);

        var second = Register(1000m, november, october);

        Assert.Equal([(october, 500m), (november, 500m)], AllocationsOf(second));
        var third = Register(100m, november, october);
        Assert.Equal([(november, 100m)], AllocationsOf(third));
    }

    // 取消は赤黒。打ち消しの入金は取り消した日(今日)の日付で、元の割当を同じ請求書・同じ額で打ち消す(2026-11-20 本人の決定)
    [Fact]
    public void 入金を取り消すと_今日の日付で打ち消しの入金と割当が立ち_請求書は未回収に戻る()
    {
        var october = October();
        var november = November();
        var receipt = Register(1500m, november, october);

        var reversal = receipt.Cancel(Today);

        Assert.Equal((Today, Money.Of(-1500m), receipt), (reversal.ReceivedOn, reversal.Amount, reversal.Reverses));
        Assert.Equal([(october, -1100m), (november, -400m)], AllocationsOf(reversal));
        Assert.Equal((Money.Of(1100m), Money.Of(2200m)), (october.Outstanding, november.Outstanding));
        Assert.True(receipt.IsCancelled);
        Assert.Equal((ReceivedOn, Money.Of(1500m)), (receipt.ReceivedOn, receipt.Amount)); // 元の入金は書き換えない
    }

    [Fact]
    public void 前受の残る入金を取り消すと_前受も打ち消される()
    {
        var october = October();
        var receipt = Register(5000m, october);

        var reversal = receipt.Cancel(Today);

        Assert.Equal((Money.Of(-1100m), Money.Of(-3900m)), (reversal.AllocatedAmount, reversal.Unallocated));
        Assert.Equal(Money.Zero, receipt.Unallocated + reversal.Unallocated);
    }

    [Fact]
    public void 取り消した入金はもう一度取り消せず_打ち消しの入金も取り消せない()
    {
        var receipt = Register(1100m, October());
        var reversal = receipt.Cancel(Today);

        Assert.Contains("取り消し済み", Assert.Throws<BusinessRuleException>(() => receipt.Cancel(Today)).Message);
        Assert.Contains("打ち消しの入金", Assert.Throws<BusinessRuleException>(() => reversal.Cancel(Today)).Message);
    }

    // 入金日は日本時間の今日まで。過去は入れられる(2026-11-20 本人の決定)
    [Fact]
    public void 入金日は今日まで入れられ_明日は拒む()
    {
        Assert.Equal(Today, Receipt.Register(HeadOffice, Today, Money.Of(1m), [], Today).ReceivedOn);

        var error = Assert.Throws<InvalidInputException>(() => Receipt.Register(HeadOffice, Today.AddDays(1), Money.Of(1m), [], Today));
        Assert.Equal("receivedOn", error.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 入金額は1円以上(decimal yen)
    {
        var error = Assert.Throws<InvalidInputException>(() => Register(yen));
        Assert.Equal("amount", error.ParamName);
    }

    // 金額の列は numeric(15,0)。上限ちょうどは受け付け、1円多ければ拒む(DB の桁あふれにしない)
    [Fact]
    public void 入金額は15桁まで()
    {
        Assert.Equal(Money.Of(999_999_999_999_999m), Register(999_999_999_999_999m).Amount);

        var error = Assert.Throws<InvalidInputException>(() => Register(1_000_000_000_000_000m));
        Assert.Equal("amount", error.ParamName);
    }

    // 入金は請求先で受ける(支店の入金は本社の請求書に充てる)
    [Fact]
    public void 支店では入金を登録できない()
    {
        var error = Assert.Throws<InvalidInputException>(() => Receipt.Register(Branch, ReceivedOn, Money.Of(1m), [], Today));
        Assert.Contains("請求先は C001", error.Message);
    }

    // 未回収の請求書を集める条件(ユースケースの検索)を誤っても、別の請求先の請求書には充てない
    [Fact]
    public void 別の請求先の請求書は渡しても充てずに止める()
    {
        var others = InvoiceOf(Other, new DateOnly(2030, 10, 31), 1000m);

        var error = Assert.Throws<BusinessRuleException>(() => Register(1100m, October(), others));
        Assert.Contains("C003", error.Message);
        Assert.Equal(Money.Zero, others.AllocatedAmount);
    }
}
