namespace SalesCore.Domain.Tests;

/// <summary>
/// 繰越の請求(請求額 ＝ 前回請求額 − 入金額 ＋ 今回売上 ＋ 消費税)、締めた時の前受の充当、回収条件と支払期限。
/// 規則の定義元は docs/domain/business-rules.md の「4. 請求」の「繰越の締め」「支払期限」「入金と消込」(2026-11-25 本人の決定)。
/// 今日(2027-01-20)は締め日・入金日とずらす(日付を取り違える誤りが見えるように)。
/// </summary>
public class CarryOverTests
{
    private static readonly TaxRate Standard = TaxRate.Of(10m);
    private static readonly Money Threshold = Money.Of(1_000_000m);
    private static readonly DateOnly Today = new(2027, 1, 20);
    private static readonly Customer HeadOffice = new("C001", "本社", 31);
    private static readonly DateOnly October = new(2026, 10, 31);
    private static readonly DateOnly November = new(2026, 11, 30);
    private static readonly DateOnly December = new(2026, 12, 31);

    // 105円 × 数量 の売上(税10%)。10箱なら今回分 1,050 ＋ 105 ＝ 1,155円
    private static SalesRecord Sale(Customer customer, DateOnly on, decimal quantity)
    {
        var line = new SalesOrderLine(quantity, 105m, Standard);
        var order = new SalesOrder(customer, on.AddDays(-5), [line], Threshold);
        order.Approve();
        return order.InstructShipment([new(line, quantity)]).Confirm(on);
    }

    private static Invoice Close(DateOnly closingDate, decimal quantity, IEnumerable<Invoice> earlier, IEnumerable<Receipt> receipts) =>
        Invoice.Create(HeadOffice, closingDate.AddDays(1 - closingDate.Day), closingDate, [Sale(HeadOffice, closingDate.AddDays(-10), quantity)], earlier, receipts);

    private static Receipt Pay(DateOnly on, decimal amount, params Invoice[] invoices) =>
        Receipt.Register(HeadOffice, on, Money.Of(amount), invoices, Today);

    private static (decimal Previous, decimal Received, decimal CarriedOver, decimal Charged, decimal Total) Amounts(Invoice i) =>
        (i.PreviousBalance.Yen, i.ReceivedAmount.Yen, i.CarriedOver.Yen, i.ChargedAmount.Yen, i.TotalAmount.Yen);

    [Fact]
    public void 最初の請求書は_前回請求額も入金額も0()
    {
        var invoice = Close(October, 10, [], []);

        Assert.Equal((0m, 0m, 0m, 1155m, 1155m), Amounts(invoice));
    }

    // 10月 1,155円に500円入金 → 11月は 前回 1,155 − 入金 500 ＝ 繰越 655、今回分 231(2箱)で 886円
    [Fact]
    public void 一部入金なら_残りを繰り越して今回分に足す()
    {
        var october = Close(October, 10, [], []);
        var paid = Pay(new DateOnly(2026, 11, 15), 500m, october);

        var november = Close(November, 2, [october], [paid]);

        Assert.Equal((1155m, 500m, 655m, 231m, 886m), Amounts(november));
    }

    [Fact]
    public void 締め日より後の入金日の入金は_今回は数えない()
    {
        var october = Close(October, 10, [], []);
        var later = Pay(new DateOnly(2026, 12, 1), 500m, october);

        var november = Close(November, 2, [october], [later]);

        Assert.Equal((1155m, 0m, 1155m, 231m, 1386m), Amounts(november));
    }

    // 締め日より後の入金日の入金は入金額に数えない(請求書の上では入金額0・請求額 1,155 のまま)が、前受として締めた請求書の今回分に充てる。
    // 今回分の未回収は0になり、入金額には次の請求書で数える(PR #30 の AIレビュー2回目の Minor。業務ルール集「入金と消込」の例)
    [Fact]
    public void 締め日より後の入金日の前受は_入金額に数えないが_締めた請求書に充てる()
    {
        var later = Pay(new DateOnly(2026, 11, 5), 1155m); // 10月を締める前に 11/5 付けで受けた(充てる請求書が無いので前受)

        var october = Close(October, 10, [], [later]);
        Receipt.AllocateAdvances([later], [october]);
        var november = Close(November, 2, [october], [later]);

        Assert.Equal((0m, 0m, 0m, 1155m, 1155m), Amounts(october));
        Assert.Equal((1155m, 0m), (october.AllocatedAmount.Yen, october.Outstanding.Yen));
        Assert.Equal((1155m, 1155m, 0m, 231m, 231m), Amounts(november));
    }

    // 入金額は「まだどの請求書にも数えていない入金」(2026-11-25 本人の決定)。前の請求書で数えた入金は数えず、
    // 前の締めの後に過去の日付で登録した入金(11/28。11月末の締めの後に登録)は、次の請求書で数える
    [Fact]
    public void 前の請求書で数えた入金は数えず_締めた後に遡って登録した入金は次で数える()
    {
        var october = Close(October, 10, [], []);
        var first = Pay(new DateOnly(2026, 11, 15), 500m, october);
        var november = Close(November, 2, [october], [first]);
        var backdated = Pay(new DateOnly(2026, 11, 28), 100m, october, november);

        var december = Close(December, 1, [november, october], [first, backdated]);

        Assert.Equal((886m, 100m, 786m, 116m, 902m), Amounts(december));
    }

    // 取り消すと、打ち消しの入金(負の額、取り消した日の日付)も入金額として数える。払われていない分が繰越に戻る
    [Fact]
    public void 取り消した入金は_打ち消しの入金を負の入金として数える()
    {
        var october = Close(October, 10, [], []);
        var paid = Pay(new DateOnly(2026, 11, 15), 500m, october);
        var november = Close(November, 2, [october], [paid]);
        var reversal = paid.Cancel(new DateOnly(2026, 12, 3));

        var december = Close(December, 1, [october, november], [paid, reversal]);

        Assert.Equal((886m, -500m, 1386m, 116m, 1502m), Amounts(december));
    }

    // 締め日(11/30)より後・締めの操作より前に取り消した入金(11/15 に500円、12/2 に取消)は、打ち消しも元の入金日で数え、
    // 11月の入金額に出さない(2026-11-25 本人の決定。PR #30 の AIレビュー)。打ち消しの入金の日付は取り消した日のまま
    [Fact]
    public void 締めの前に取り消した入金は_打ち消しも元の入金日で数えて入金額に出さない()
    {
        var october = Close(October, 10, [], []);
        var paid = Pay(new DateOnly(2026, 11, 15), 500m, october);
        var reversal = paid.Cancel(new DateOnly(2026, 12, 2));

        var november = Close(November, 2, [october], [paid, reversal]);

        Assert.Equal(new DateOnly(2026, 12, 2), reversal.ReceivedOn);
        Assert.Equal((1155m, 0m, 1155m, 231m, 1386m), Amounts(november));
    }

    [Fact]
    public void 元の入金を読み込まずに打ち消しの入金を渡すと止める()
    {
        var october = Close(October, 10, [], []);
        var paid = Pay(new DateOnly(2026, 11, 15), 500m, october);
        var reversal = paid.Cancel(new DateOnly(2026, 12, 2));
        typeof(Receipt).GetProperty(nameof(Receipt.Reverses))!.SetValue(reversal, null); // DB から元の入金を読まずに読んだ打ち消し
        typeof(Receipt).GetProperty(nameof(Receipt.ReversesId))!.SetValue(reversal, 1L);

        Assert.Throws<InvalidOperationException>(() => Close(November, 2, [october], [paid, reversal]));
    }

    // 過入金: 前回 886 − 入金 2,000 ＝ 繰越 −1,114。今回分 116 が正なので、請求額 −998円(支払い不要)でも作る(2026-11-25 本人の決定)
    [Fact]
    public void 過入金で請求額が0円以下でも_今回分が正なら請求書を作る()
    {
        var october = Close(October, 10, [], []);
        var first = Pay(new DateOnly(2026, 11, 15), 500m, october);
        var november = Close(November, 2, [october], [first]);
        var over = Pay(new DateOnly(2026, 12, 20), 2000m, october, november);

        var december = Close(December, 1, [october, november], [first, over]);

        Assert.Equal((886m, 2000m, -1114m, 116m, -998m), Amounts(december));
    }

    // 返品だけの月は今回分が負。繰越(10月の 231円)があって請求額(231 − 116 ＝ 115円)が正でも、今回分が0円以下なら作らず、売上を次の締めに回す
    [Fact]
    public void 今回分が0円以下なら_繰越があっても請求書を作らない()
    {
        var line = new SalesOrderLine(2, 105m, Standard);
        var order = new SalesOrder(HeadOffice, new DateOnly(2026, 10, 1), [line], Threshold);
        order.Approve();
        var shipped = order.InstructShipment([new(line, 2)]).Confirm(new DateOnly(2026, 10, 20));
        var october = Invoice.Create(HeadOffice, new DateOnly(2026, 10, 1), October, [shipped], [], []);
        var returned = order.RecordReturn(new DateOnly(2026, 11, 10), [new(line, 1)]); // −105、税 −10.5 → −11

        var error = Assert.Throws<BusinessRuleException>(() => Invoice.Create(HeadOffice, new DateOnly(2026, 11, 1), November, [returned], [october], []));

        Assert.Contains("今回分が -116 円", error.Message);
        Assert.False(returned.IsBilled);
    }

    [Fact]
    public void 前回は締め日の一番新しい請求書()
    {
        var october = Close(October, 10, [], []);
        var september = Invoice.Create(HeadOffice, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), [Sale(HeadOffice, new DateOnly(2026, 9, 10), 1)], [], []);

        var november = Close(November, 2, [october, september], []);

        Assert.Equal(1155m, november.PreviousBalance.Yen);
    }

    [Fact]
    public void 締め日が今回以後の請求書は_前の請求書として渡せない()
    {
        var november = Close(November, 2, [], []);

        Assert.Throws<BusinessRuleException>(() => Close(October, 10, [november], []));
    }

    [Fact]
    public void 別の請求先の請求書や入金は_繰越に入れない()
    {
        var other = new Customer("C020", "別の得意先", 31);
        var theirs = Invoice.Create(other, new DateOnly(2026, 10, 1), October, [Sale(other, new DateOnly(2026, 10, 10), 1)], [], []);
        var theirReceipt = Receipt.Register(other, new DateOnly(2026, 11, 1), Money.Of(100m), [theirs], Today);

        Assert.Throws<BusinessRuleException>(() => Close(November, 2, [theirs], []));
        Assert.Throws<BusinessRuleException>(() => Close(November, 2, [], [theirReceipt]));
    }

    // --- 締めた時の前受の充当 ---

    // 前受は古い入金から、未回収の請求書に締め日の古い順に充てる。取り消した入金と打ち消しの入金は充てない
    [Fact]
    public void 前受は古い入金から未回収の古い請求書へ充て_取り消した入金と打ち消しは充てない()
    {
        var october = Close(October, 10, [], []);   // 1,155
        var advanceOld = Pay(new DateOnly(2026, 10, 20), 1155m + 300m, october);   // 10月を消し、前受 300
        var advanceNew = Pay(new DateOnly(2026, 11, 5), 400m);                      // 前受 400
        var cancelled = Pay(new DateOnly(2026, 10, 25), 50m);                       // 前受 50 → 取り消す
        var reversal = cancelled.Cancel(new DateOnly(2026, 10, 26)); // 打ち消し(負の前受)を、前受の入金(11/5)より前の日付にする
        // 今回分 578(5箱 525 ＋ 税53)は前受の合計 700 より少ないので、どちらの入金から充てるかで残りが変わる
        var november = Close(November, 5, [october], [advanceOld, advanceNew, cancelled, reversal]);

        Receipt.AllocateAdvances([advanceNew, reversal, cancelled, advanceOld], [november, october]);

        Assert.Equal((0m, 700m - 578m), (advanceOld.Unallocated.Yen, advanceNew.Unallocated.Yen)); // 古い入金(10/20)から
        Assert.Equal(0m, november.Outstanding.Yen);
        Assert.Equal((50m, -50m), (cancelled.Unallocated.Yen, reversal.Unallocated.Yen)); // 取り消した前受は残ったまま(足して0)
    }

    // 入金の取消で古い請求書が未回収に戻っていたら、締めた時の前受はそちらを先に消す
    [Fact]
    public void 未回収に戻った古い請求書があれば_そちらに先に充てる()
    {
        var october = Close(October, 10, [], []);   // 1,155
        var paid = Pay(new DateOnly(2026, 11, 1), 1155m, october);
        var advance = Pay(new DateOnly(2026, 11, 2), 1000m); // 全部前受
        paid.Cancel(new DateOnly(2026, 11, 3));              // 10月が未回収に戻る(取消では前受を充て直さない)
        var november = Close(November, 2, [october], []);    // 231

        Receipt.AllocateAdvances([advance], [november, october]);

        Assert.Equal((155m, 231m), (october.Outstanding.Yen, november.Outstanding.Yen));
        Assert.Equal(0m, advance.Unallocated.Yen);
    }

    // 繰越のある請求書(11月: 繰越 655 ＋ 今回分 231)にも、充てるのは今回分まで(Day55 の T6 を確かめる)
    [Fact]
    public void 繰越のある請求書にも_今回分を超えて充てない()
    {
        var october = Close(October, 10, [], []);
        var first = Pay(new DateOnly(2026, 11, 15), 500m, october);
        var november = Close(November, 2, [october], [first]);
        Assert.Equal(886m, november.TotalAmount.Yen);

        var over = Pay(new DateOnly(2026, 12, 20), 5000m, november);

        Assert.Equal((231m, 0m), (november.AllocatedAmount.Yen, november.Outstanding.Yen));
        Assert.Equal(5000m - 231m, over.Unallocated.Yen);
    }

    [Fact]
    public void 別の請求先の入金や請求書には_前受を充てない()
    {
        var other = new Customer("C020", "別の得意先", 31);
        var theirReceipt = Receipt.Register(other, new DateOnly(2026, 11, 1), Money.Of(100m), [], Today);
        var october = Close(October, 10, [], []);

        Assert.Throws<BusinessRuleException>(() => Receipt.AllocateAdvances([theirReceipt], [october]));
    }

    // --- 回収条件と支払期限(何か月後の何日。2026-11-25 本人の決定) ---

    [Theory]
    [InlineData(31, 1, 31, "2026-10-31", "2026-11-30")] // 月末締め翌月末
    [InlineData(20, 1, 10, "2026-10-20", "2026-11-10")] // 20日締め翌月10日
    [InlineData(31, 1, 31, "2027-01-31", "2027-02-28")] // 翌月が2月
    [InlineData(31, 1, 31, "2028-01-31", "2028-02-29")] // うるう年
    [InlineData(31, 1, 25, "2026-12-31", "2027-01-25")] // 年をまたぐ
    [InlineData(15, 0, 31, "2026-11-15", "2026-11-30")] // 15日締め当月末
    [InlineData(31, 2, 25, "2026-12-31", "2027-02-25")] // 翌々月
    [InlineData(31, 3, 5, "2026-11-30", "2027-02-05")]  // 3か月後
    public void 支払期限は_締め日の月から回収月だけ後の月の回収日(int closingDay, int months, int day, string closingDate, string expected)
    {
        var customer = new Customer("C001", "本社", closingDay, collectionMonths: months, collectionDay: day);

        Assert.Equal(DateOnly.Parse(expected), customer.DueDateFor(DateOnly.Parse(closingDate)));
    }

    [Fact]
    public void 回収条件を省くと翌月末()
    {
        var customer = new Customer("C001", "本社", 20);

        Assert.Equal((1, 31), (customer.CollectionMonths, customer.CollectionDay));
    }

    [Theory]
    [InlineData(31, -1, 31)]
    [InlineData(31, 4, 31)]
    [InlineData(31, 1, 0)]
    [InlineData(31, 1, 7)]
    [InlineData(31, 0, 31)] // 月末締め当月末: 支払期限が締め日になる
    [InlineData(15, 0, 15)] // 15日締め当月15日
    [InlineData(15, 0, 10)]
    public void 選べない回収条件は拒む(int closingDay, int months, int day)
    {
        Assert.Throws<InvalidInputException>(() => new Customer("C001", "本社", closingDay, collectionMonths: months, collectionDay: day));
    }

    // 支店の請求書は請求先で締めるので、請求先の回収条件を使う
    [Fact]
    public void 請求書の支払期限は_請求先の回収条件で決まる()
    {
        var headOffice = new Customer("C001", "本社", 20, collectionMonths: 1, collectionDay: 10);
        var branch = new Customer("C002", "大阪支店", 31, headOffice, collectionMonths: 3, collectionDay: 5);
        var closingDate = new DateOnly(2026, 11, 20);

        var invoice = Invoice.Create(headOffice, new DateOnly(2026, 10, 21), closingDate, [Sale(branch, new DateOnly(2026, 11, 10), 1)], [], []);

        Assert.Equal(new DateOnly(2026, 12, 10), invoice.DueDate);
    }
}
