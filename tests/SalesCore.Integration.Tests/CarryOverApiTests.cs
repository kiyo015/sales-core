using System.Net;
using Microsoft.EntityFrameworkCore;
using SalesCore.Application;
using SalesCore.Domain;

namespace SalesCore.Integration.Tests;

/// <summary>
/// 繰越の請求(Day57 の完了条件)。入金の前後・一部入金・過入金・返品だけの月で、請求額が手で計算した値と一致する。
/// 規則の定義元は docs/domain/business-rules.md の「4. 請求」の「繰越の締め」「支払期限」「入金と消込」(2026-11-25 本人の決定)。
/// </summary>
public sealed class CarryOverApiTests : ApiTest
{
    public CarryOverApiTests(DatabaseFixture fixture) : base(fixture)
    {
        Now = new DateTimeOffset(2027, 3, 1, 9, 0, 0, TimeSpan.FromHours(9));
    }

    private static readonly DateOnly October = new(2026, 10, 31);
    private static readonly DateOnly November = new(2026, 11, 30);
    private static readonly DateOnly December = new(2026, 12, 31);

    // 支店(C002、請求先は本社 C001・月末締め翌月末)にボルト(105円・10%)を出す。手で計算した値:
    //   10月: 10箱 1,050 ＋ 税105 ＝ 今回分 1,155。前回0・入金0 → 請求額 1,155。11/15 に 500円入金(一部)
    //   11月: 2箱 231。前回 1,155 − 入金 500 ＝ 繰越 655 → 886。締めた後に 11/28 付けの 100円を登録(入金の前後)、12/20 に 2,000円(過入金)
    //   12月: 1箱 116。前回 886 − 入金 2,100 ＝ 繰越 −1,214 → 請求額 −1,098(支払い不要でも作る)。前受を12月に充てる
    //    1月: 10月の2箱を返品(−210、税 −21)。今回分 −231 なので締めず、売上を次に回す
    //    2月: 20箱 2,100 と返品 −210 → 1,890 ＋ 税189 ＝ 今回分 2,079。前回 −1,098 − 入金0 → 981。前受 1,098 を2月に充てる
    [Fact]
    public async Task 入金の前後_一部入金_過入金_返品だけの月で_請求額が手で計算した値になる()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var api = Api();
        var octoberOrder = await Order(api, new DateOnly(2026, 10, 1), 12m);
        await Ship(api, octoberOrder.Id, new DateOnly(2026, 10, 10), (octoberOrder.Lines[0].Id, 10m));

        var october = await Close(api, October);
        Assert.Equal((0m, 0m, 0m, 1155m, new DateOnly(2026, 11, 30)), (october.PreviousBalance, october.ReceivedAmount, october.CarriedOver, october.TotalAmount, october.DueDate));

        await Pay(api, new DateOnly(2026, 11, 15), 500m);
        var novemberOrder = await Order(api, new DateOnly(2026, 11, 1), 2m);
        await Ship(api, novemberOrder.Id, new DateOnly(2026, 11, 10), (novemberOrder.Lines[0].Id, 2m));

        var november = await Close(api, November);
        Assert.Equal((1155m, 500m, 655m, 231m, 886m), (november.PreviousBalance, november.ReceivedAmount, november.CarriedOver, november.SalesAmount + november.TaxAmount, november.TotalAmount));
        Assert.Equal(new DateOnly(2026, 12, 31), november.DueDate);

        await Pay(api, new DateOnly(2026, 11, 28), 100m); // 11月の締めの後に、締め日より前の日付で登録
        var over = await Pay(api, new DateOnly(2026, 12, 20), 2000m);
        var decemberOrder = await Order(api, new DateOnly(2026, 12, 1), 1m);
        await Ship(api, decemberOrder.Id, new DateOnly(2026, 12, 10), (decemberOrder.Lines[0].Id, 1m));

        var december = await Close(api, December);
        Assert.Equal((886m, 2100m, -1214m, 116m, -1098m), (december.PreviousBalance, december.ReceivedAmount, december.CarriedOver, december.SalesAmount + december.TaxAmount, december.TotalAmount));
        Assert.Equal((116m, 0m), (december.AllocatedAmount, december.Outstanding)); // 締めた時に前受を充てた
        // 繰越のある11月(請求額 886)にも、充てたのは今回分の 231円まで(Day55 の T6)
        var novemberNow = await Get<InvoiceView>(api, $"/invoices/{november.Id}");
        Assert.Equal((231m, 0m), (novemberNow.AllocatedAmount, novemberNow.Outstanding));
        Assert.Equal(2000m - 555m - 231m - 116m, (await Get<ReceiptView>(api, $"/receipts/{over.Id}")).Unallocated);

        // 1月: 10月の2箱を返品するだけの月は締めない
        var scenario = new TestScenario(Db);
        var loaded = await Db.SalesOrders.Include(o => o.Lines).Include(o => o.Customer).ThenInclude(c => c.BillingCustomer).SingleAsync(o => o.Id == octoberOrder.Id);
        var returned = scenario.AddSalesRecord(loaded.RecordReturn(new DateOnly(2027, 1, 10), [new(loaded.Lines[0], 2m)]));
        await scenario.EndRequestAsync();
        var error = await AssertProblem(api, "/invoices", HttpStatusCode.Conflict, new CloseInvoice("C001", new DateOnly(2027, 1, 31)));
        Assert.Contains("今回分が -231 円", error.Detail);

        var februaryOrder = await Order(api, new DateOnly(2027, 2, 1), 20m);
        var february = await Ship(api, februaryOrder.Id, new DateOnly(2027, 2, 3), (februaryOrder.Lines[0].Id, 20m));
        var feb = await Close(api, new DateOnly(2027, 2, 28));
        Assert.Equal((1890m, 189m), (feb.SalesAmount, feb.TaxAmount));
        Assert.Equal((-1098m, 0m, -1098m, 981m), (feb.PreviousBalance, feb.ReceivedAmount, feb.CarriedOver, feb.TotalAmount));
        Assert.Equal((1098m, 981m), (feb.AllocatedAmount, feb.Outstanding));
        Assert.Equal(new DateOnly(2027, 3, 31), feb.DueDate);
        Assert.Equal(2, feb.SalesRecordNumbers.Count); // 2月の出荷と、1月に回した返品
        Assert.Equal(0m, (await Get<ReceiptView>(api, $"/receipts/{over.Id}")).Unallocated);
        Assert.Equivalent(feb, await Get<InvoiceView>(api, $"/invoices/{feb.Id}"), strict: true);
    }

    // 締めの後に入金を取り消すと、打ち消しの入金(取り消した日の日付)が次の請求書の入金額から引かれ、払われていない分が繰越に戻る
    [Fact]
    public async Task 入金を取り消すと_次の請求書で入金額から引いて繰り越す()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var api = Api();
        var order = await Order(api, new DateOnly(2026, 10, 1), 11m);
        await Ship(api, order.Id, new DateOnly(2026, 10, 10), (order.Lines[0].Id, 10m));
        await Close(api, October); // 1,155
        var paid = await Pay(api, new DateOnly(2026, 11, 15), 1155m);
        await Ship(api, order.Id, new DateOnly(2026, 11, 10), (order.Lines[0].Id, 1m));
        var november = await Close(api, November); // 前回 1,155 − 入金 1,155 ＋ 116 ＝ 116
        Assert.Equal(116m, november.TotalAmount);

        await Send<ReceiptView>(api, $"/receipts/{paid.Id}/cancel", expected: HttpStatusCode.Created); // 打ち消しの入金は今日(2027-03-01)の日付
        var march = await Order(api, new DateOnly(2027, 3, 1), 1m);
        await Ship(api, march.Id, new DateOnly(2027, 3, 1), (march.Lines[0].Id, 1m));
        Now = new DateTimeOffset(2027, 3, 31, 9, 0, 0, TimeSpan.FromHours(9));

        var invoice = await Close(Api(), new DateOnly(2027, 3, 31));

        // 入金額 ＝ 3月末までの入金の合計(1,155 − 1,155 ＝ 0) − 前に数えた入金額(11月の 1,155) ＝ −1,155。繰越 ＝ 116 − (−1,155)
        Assert.Equal((116m, -1155m, 1271m, 116m, 1387m),
            (invoice.PreviousBalance, invoice.ReceivedAmount, invoice.CarriedOver, invoice.SalesAmount + invoice.TaxAmount, invoice.TotalAmount));
    }

    // 締め日(11/30)の後・締めの前(12/2)に取り消した11月の入金は、11月の請求書の入金額に出さない(2026-11-25 本人の決定)。
    // 締めでは元の入金も読むので、打ち消しの入金から元の入金日が分かる
    [Fact]
    public async Task 締めの前に取り消した入金は_その請求書の入金額に出さない()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var api = Api();
        var order = await Order(api, new DateOnly(2026, 10, 1), 12m);
        await Ship(api, order.Id, new DateOnly(2026, 10, 10), (order.Lines[0].Id, 10m));
        await Close(api, October); // 1,155
        var paid = await Pay(api, new DateOnly(2026, 11, 15), 500m);
        await Ship(api, order.Id, new DateOnly(2026, 11, 10), (order.Lines[0].Id, 2m));
        Now = new DateTimeOffset(2026, 12, 2, 9, 0, 0, TimeSpan.FromHours(9));
        await Send<ReceiptView>(Api(), $"/receipts/{paid.Id}/cancel", expected: HttpStatusCode.Created);
        Now = new DateTimeOffset(2026, 12, 5, 9, 0, 0, TimeSpan.FromHours(9));

        var november = await Close(Api(), November);

        Assert.Equal((1155m, 0m, 1155m, 1386m), (november.PreviousBalance, november.ReceivedAmount, november.CarriedOver, november.TotalAmount));
    }

    // 締めで読む入金は、締める請求先のものだけ(別の会社 C003 の入金を数えたり充てたりしない)
    [Fact]
    public async Task 別の請求先の入金は_入金額にも前受の充当にも入らない()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var api = Api();
        var others = await Send<ReceiptView>(api, "/receipts", new RegisterReceipt("C003", new DateOnly(2026, 10, 20), 500m), HttpStatusCode.Created);
        var order = await Order(api, new DateOnly(2026, 10, 1), 10m);
        await Ship(api, order.Id, new DateOnly(2026, 10, 10), (order.Lines[0].Id, 10m));

        var october = await Close(api, October);

        Assert.Equal((0m, 1155m, 1155m), (october.ReceivedAmount, october.TotalAmount, october.Outstanding));
        Assert.Equal(500m, (await Get<ReceiptView>(api, $"/receipts/{others.Id}")).Unallocated);
    }

    // 支払期限は請求先の回収条件で決める(20日締め翌月10日)。支店の回収条件(3か月後の5日)は使わない
    [Fact]
    public async Task 支払期限は請求先の回収条件で決まり_回収条件は保存して読み直しても同じ()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var twentieth = new Customer("C010", "二十日締め", 20, collectionMonths: 1, collectionDay: 10);
        Db.Add(new Customer("C011", "二十日締めの支店", 31, twentieth, collectionMonths: 3, collectionDay: 5));
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        var api = Api();
        var order = await Send<SalesOrderView>(api, "/sales-orders",
            new CreateSalesOrder("C011", new DateOnly(2026, 11, 1), [new CreateSalesOrderLine("P001", 1m)]), HttpStatusCode.Created);
        await Send<SalesOrderView>(api, $"/sales-orders/{order.Id}/approve");
        await Ship(api, order.Id, new DateOnly(2026, 11, 10), (order.Lines[0].Id, 1m));

        var invoice = await Send<InvoiceView>(api, "/invoices", new CloseInvoice("C010", new DateOnly(2026, 11, 20)), HttpStatusCode.Created);

        Assert.Equal(new DateOnly(2026, 12, 10), invoice.DueDate);
        var saved = await Db.Customers.SingleAsync(c => c.Code == "C011");
        Assert.Equal((3, 5), (saved.CollectionMonths, saved.CollectionDay));
    }

    // 同じ請求先の締めが同時に走った(10/31 締めを確かめている間に、別の画面で 11/30 締めが先に済んだ)。どちらも10月の売上を入れるので、
    // 後の方は売上の版数で止まる(二重に請求しない。PR #30 の AIレビュー2回目の Minor。月次締めで締めの順序が大事になる前に)
    [Fact]
    public async Task 同じ請求先の締めが同時なら_後の方は売上の版数で止まる()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var api = Api();
        var order = await Order(api, new DateOnly(2026, 10, 1), 10m);
        await Ship(api, order.Id, new DateOnly(2026, 10, 10), (order.Lines[0].Id, 10m));
        var other = new InvoiceUseCases(Sessions(User), new FixedClock(Now), User!);
        var racing = new InvoiceUseCases(new TestSessions(() => CreateAnotherSession(User,
            new BeforeSave(() => other.CloseAsync(new CloseInvoice("C001", November))))), new FixedClock(Now), User!);

        var error = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => racing.CloseAsync(new CloseInvoice("C001", October)));

        Assert.Equal(409, OperationError.From(error)?.Status);
    }

    private static async Task<SalesOrderView> Order(HttpClient api, DateOnly orderedOn, decimal boxes)
    {
        var order = await CreateOrder(api, orderedOn, new CreateSalesOrderLine("P001", boxes));
        return await Send<SalesOrderView>(api, $"/sales-orders/{order.Id}/approve");
    }

    private static Task<InvoiceView> Close(HttpClient api, DateOnly closingDate) =>
        Send<InvoiceView>(api, "/invoices", new CloseInvoice("C001", closingDate), HttpStatusCode.Created);

    private static Task<ReceiptView> Pay(HttpClient api, DateOnly receivedOn, decimal amount) =>
        Send<ReceiptView>(api, "/receipts", new RegisterReceipt("C001", receivedOn, amount), HttpStatusCode.Created);
}
