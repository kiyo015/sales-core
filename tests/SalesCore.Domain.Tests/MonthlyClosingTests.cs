namespace SalesCore.Domain.Tests;

/// <summary>
/// 月次締めと売掛金の残高(期首 ＋ 売上 ＋ 消費税 − 入金 ＝ 期末)、締めた月の日付を止める判断。
/// 規則の定義元は docs/domain/business-rules.md の「4. 請求」の「月次締め」(2026-11-12・11-26 本人の決定)。
/// 売上は売上日の月に税抜で、消費税は請求書の締め日の月に、入金は入金日の月に入れる。
/// </summary>
public class MonthlyClosingTests
{
    private static readonly TaxRate Standard = TaxRate.Of(10m);
    private static readonly Money Threshold = Money.Of(1_000_000m);
    private static readonly DateOnly Today = new(2027, 1, 20);
    private static readonly Customer HeadOffice = new("C001", "本社", 31);
    private static readonly Customer Branch = new("C002", "支店", 31, HeadOffice);
    private static readonly Customer Twentieth = new("C010", "二十日締め", 20);

    // 105円 × 数量 の売上(税10%)。10箱なら 1,050 ＋ 税105
    private static (SalesRecord Record, SalesOrder Order) Ship(Customer customer, DateOnly on, decimal quantity)
    {
        var line = new SalesOrderLine(quantity, 105m, Standard);
        var order = new SalesOrder(customer, on.AddDays(-5), [line], Threshold);
        order.Approve();
        return (order.InstructShipment([new(line, quantity)]).Confirm(on), order);
    }

    private static SalesRecord Sale(Customer customer, DateOnly on, decimal quantity) => Ship(customer, on, quantity).Record;

    private static Invoice Bill(Customer billing, DateOnly closingDate, params SalesRecord[] records) =>
        Invoice.Create(billing, closingDate.AddDays(1 - closingDate.Day), closingDate, records, [], []);

    private static Receipt Pay(Customer billing, DateOnly on, decimal amount) => Receipt.Register(billing, on, Money.Of(amount), [], Today);

    private static MonthlyClosing Close(int year, int month, MonthlyClosing? previous, IEnumerable<SalesRecord> sales,
        IEnumerable<Invoice> invoices, IEnumerable<Receipt> receipts, DateOnly? first = null, DateOnly? today = null) =>
        MonthlyClosing.Close(year, month, today ?? Today, previous, first ?? new DateOnly(year, month, 1), sales, invoices, receipts);

    private static (string Code, decimal Opening, decimal Sales, decimal Tax, decimal Received, decimal Closing) Row(ReceivableBalance b) =>
        (b.BillingCustomer.Code, b.Opening.Yen, b.Sales.Yen, b.Tax.Yen, b.Received.Yen, b.Closing.Yen);

    // 10月: 支店に10箱(10/10)、10/31 締めの請求書(税105)、10/20 に300円入金 → 本社の期末 0 ＋ 1,050 ＋ 105 − 300 ＝ 855
    [Fact]
    public void 期末は期首と売上と消費税から入金を引いた額で_支店の売上は請求先の残高に入る()
    {
        var sale = Sale(Branch, new DateOnly(2026, 10, 10), 10m);
        var invoice = Bill(HeadOffice, new DateOnly(2026, 10, 31), sale);
        var paid = Pay(HeadOffice, new DateOnly(2026, 10, 20), 300m);

        var october = Close(2026, 10, null, [sale], [invoice], [paid]);

        Assert.Equal(new DateOnly(2026, 10, 1), october.Month);
        Assert.Equal(("C001", 0m, 1050m, 105m, 300m, 855m), Row(Assert.Single(october.Balances)));
    }

    // 売上は売上日の月、消費税は請求書の締め日の月。20日締めの請求先の10/25 の売上は10月の売上で、税は11/20 締めの11月に入る
    [Fact]
    public void 消費税は請求書の締め日の月に入り_未請求の売上は税抜で残高に入る()
    {
        var late = Sale(Twentieth, new DateOnly(2026, 10, 25), 10m);
        var october = Close(2026, 10, null, [late], [], []);
        var invoice = Bill(Twentieth, new DateOnly(2026, 11, 20), late);

        var november = Close(2026, 11, october, [late], [invoice], []);

        Assert.Equal(("C010", 0m, 1050m, 0m, 0m, 1050m), Row(Assert.Single(october.Balances)));
        Assert.Equal(("C010", 1050m, 0m, 105m, 0m, 1155m), Row(Assert.Single(november.Balances)));
    }

    // 月の末日の売上・入金はその月、翌月1日は翌月。前の月の末日の売上は前の月に入っているので数えない
    [Fact]
    public void 月の末日はその月に入り_翌月1日と前の月の末日は入らない()
    {
        var lastOfOctober = Sale(HeadOffice, new DateOnly(2026, 10, 31), 1m);
        var octoberInvoice = Bill(HeadOffice, new DateOnly(2026, 10, 31), lastOfOctober);
        var receipts = new[] { Pay(HeadOffice, new DateOnly(2026, 10, 31), 1m), Pay(HeadOffice, new DateOnly(2026, 11, 30), 10m), Pay(HeadOffice, new DateOnly(2026, 12, 1), 100m) };
        var october = Close(2026, 10, null, [lastOfOctober], [octoberInvoice], receipts);
        var firstOfNovember = Sale(HeadOffice, new DateOnly(2026, 11, 1), 2m);
        var lastOfNovember = Sale(HeadOffice, new DateOnly(2026, 11, 30), 3m);
        var novemberInvoice = Bill(HeadOffice, new DateOnly(2026, 11, 30), firstOfNovember, lastOfNovember);
        var firstOfDecember = Sale(HeadOffice, new DateOnly(2026, 12, 1), 4m);

        var november = Close(2026, 11, october, [lastOfOctober, firstOfNovember, lastOfNovember, firstOfDecember],
            [octoberInvoice, novemberInvoice], receipts);

        // 10月の期末 116 − 1 ＝ 115。11月は 210 ＋ 315 ＝ 525、税53(525 × 10% ＝ 52.5 → 53)、入金は11/30 の10円だけ
        Assert.Equal(("C001", 115m, 525m, 53m, 10m, 683m), Row(Assert.Single(november.Balances)));
    }

    // 入金の取消は赤黒(打ち消しの入金は取り消した日の日付)。元の入金の月の入金はそのままで、取り消した日の月で引く
    [Fact]
    public void 取り消した入金は元の月で入金に入り_打ち消しは取り消した日の月で引く()
    {
        var paid = Pay(HeadOffice, new DateOnly(2026, 10, 20), 500m);
        var reversal = paid.Cancel(new DateOnly(2026, 11, 5));
        var october = Close(2026, 10, null, [], [], [paid, reversal]);

        var november = Close(2026, 11, october, [], [], [paid, reversal]);

        Assert.Equal(("C001", 0m, 0m, 0m, 500m, -500m), Row(Assert.Single(october.Balances)));
        Assert.Equal(("C001", -500m, 0m, 0m, -500m, 0m), Row(Assert.Single(november.Balances)));
    }

    // 期首があれば取引が無くても行を作る(残高の一覧に出る)。期首も月の額も全部0なら作らない(同じ月に入金して取り消した C001)
    [Fact]
    public void 期首があれば取引が無くても行を作り_期首も月の額も0なら作らない()
    {
        var paid = Pay(HeadOffice, new DateOnly(2026, 10, 20), 500m);
        var other = Pay(Twentieth, new DateOnly(2026, 10, 25), 100m);
        var settled = paid.Cancel(new DateOnly(2026, 10, 30));
        var october = Close(2026, 10, null, [], [], [paid, other, settled]);

        var november = Close(2026, 11, october, [], [], [paid, other, settled]);

        Assert.Equal(("C010", 0m, 0m, 0m, 100m, -100m), Row(Assert.Single(october.Balances)));
        Assert.Equal(("C010", -100m, 0m, 0m, 0m, -100m), Row(Assert.Single(november.Balances)));
    }

    // 締められるのは終わった月だけ(末日が今日より前)。今日が末日なら、その月はまだ締められない
    [Fact]
    public void 終わっていない月は締められない()
    {
        var error = Assert.Throws<InvalidInputException>(() => Close(2026, 11, null, [], [], [], today: new DateOnly(2026, 11, 30)));
        Assert.Contains("2026年11月はまだ終わっていない", error.Message);

        Assert.Equal(new DateOnly(2026, 11, 1), Close(2026, 11, null, [], [], [], today: new DateOnly(2026, 12, 1)).Month);
    }

    [Fact]
    public void 前の月を締めていなければ締められず_締めた月はもう一度締められない()
    {
        var october = Close(2026, 10, null, [], [], []);

        var skipped = Assert.Throws<BusinessRuleException>(() => Close(2026, 12, october, [], [], []));
        var again = Assert.Throws<BusinessRuleException>(() => Close(2026, 10, october, [], [], []));

        Assert.Contains("2026年11月を先に締める", skipped.Message);
        Assert.Contains("2026年10月まで締め済み", again.Message);
    }

    // 最初に締める月は、取引(売上・入金)の一番古い月(2026-11-26 本人の決定)。期首は0
    [Fact]
    public void 最初に締める月は取引の一番古い月で_取引が無ければ締められない()
    {
        var later = Assert.Throws<BusinessRuleException>(() => Close(2026, 11, null, [], [], [], first: new DateOnly(2026, 10, 31)));
        var earlier = Assert.Throws<BusinessRuleException>(() => Close(2026, 9, null, [], [], [], first: new DateOnly(2026, 10, 1)));
        var none = Assert.Throws<BusinessRuleException>(() => MonthlyClosing.Close(2026, 10, Today, null, null, [], [], []));

        Assert.Contains("取引の一番古い月(2026年10月)から締める", later.Message);
        Assert.Contains("取引の一番古い月(2026年10月)から締める", earlier.Message);
        Assert.Contains("締める取引(売上・入金)が無い", none.Message);
    }

    // その月の締め日の請求書を締めていない請求先があれば、月次締めを止める(2026-11-26 本人の決定)。請求先と締め日を理由に出す
    [Fact]
    public void その月の締め日までの未請求の売上を締めていない請求先があれば締めない()
    {
        var unbilled = Sale(Twentieth, new DateOnly(2026, 11, 20), 1m);

        var error = Assert.Throws<BusinessRuleException>(() => Close(2026, 11, null, [unbilled], [], []));

        Assert.Contains("C010(2026-11-20 締め)", error.Message);
    }

    // 一番起きやすい締め忘れ: 先月(10/31)は締めたが、今月(11/30)の請求書を忘れた。前の締め日の請求書があっても止める(PR #31 の AIレビュー2回目)
    [Fact]
    public void 前の月の請求書があっても_その月の締め日の請求書を締めていなければ締めない()
    {
        var october = Sale(HeadOffice, new DateOnly(2026, 10, 10), 1m);
        var octoberInvoice = Bill(HeadOffice, new DateOnly(2026, 10, 31), october);
        var octoberClosing = Close(2026, 10, null, [october], [octoberInvoice], []);
        var november = Sale(HeadOffice, new DateOnly(2026, 11, 10), 1m);

        var error = Assert.Throws<BusinessRuleException>(() => Close(2026, 11, octoberClosing, [october, november], [octoberInvoice], []));

        Assert.Contains("C001(2026-11-30 締め)", error.Message);
    }

    // 締め日(20日)より後の売上は次の締め日の請求書に入るので、未請求でも止めない
    [Fact]
    public void 締め日より後の未請求の売上は止めない()
    {
        var afterClosing = Sale(Twentieth, new DateOnly(2026, 11, 21), 1m);

        var november = Close(2026, 11, null, [afterClosing], [], []);

        Assert.Equal(("C010", 0m, 105m, 0m, 0m, 105m), Row(Assert.Single(november.Balances)));
    }

    // 請求の締めの後に、締め日以前の日付で入れた売上は次の請求書に入る(業務ルール集「月次締め」)。締め日以後の請求書があれば止めない
    [Fact]
    public void その月の締め日の請求書があれば_後から入れた締め日以前の売上は止めない()
    {
        var billed = Sale(Twentieth, new DateOnly(2026, 11, 10), 1m);
        var invoice = Bill(Twentieth, new DateOnly(2026, 11, 20), billed);
        var backdated = Sale(Twentieth, new DateOnly(2026, 11, 15), 2m);

        var november = Close(2026, 11, null, [billed, backdated], [invoice], []);

        Assert.Equal(("C010", 0m, 315m, 11m, 0m, 326m), Row(Assert.Single(november.Balances)));
    }

    // 今回分が0円以下(返品だけ)の売上は締めずに次に回すので(「繰越の締め」)、未請求でも止めない
    [Fact]
    public void 今回分が0円以下で締めない売上だけなら止めない()
    {
        var (sale, order) = Ship(HeadOffice, new DateOnly(2026, 10, 10), 10m);
        var invoice = Bill(HeadOffice, new DateOnly(2026, 10, 31), sale);
        var october = Close(2026, 10, null, [sale], [invoice], []);
        var returned = order.RecordReturn(new DateOnly(2026, 11, 10), [new(order.Lines[0], 2m)]);

        var november = Close(2026, 11, october, [sale, returned], [invoice], []);

        Assert.Equal(("C001", 1155m, -210m, 0m, 0m, 945m), Row(Assert.Single(november.Balances)));
    }

    // 月次締めの後は、締めた月の日付(末日まで)を止める。翌月1日からは入れられる。締めていなければ止めない
    [Fact]
    public void 締めた月の末日までの日付は止め_翌月1日は通す()
    {
        var october = Close(2026, 10, null, [], [], []);

        var error = Assert.Throws<BusinessRuleException>(() => MonthlyClosing.EnsureOpen(october, new DateOnly(2026, 10, 31), "出荷日"));
        MonthlyClosing.EnsureOpen(october, new DateOnly(2026, 11, 1), "出荷日");
        MonthlyClosing.EnsureOpen(null, new DateOnly(2026, 10, 31), "出荷日");

        Assert.Contains("出荷日 2026-10-31 は、月次締めした2026年10月以前の日付", error.Message);
    }
}
