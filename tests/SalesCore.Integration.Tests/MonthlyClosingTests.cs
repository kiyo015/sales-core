using System.Net;
using Microsoft.EntityFrameworkCore;
using SalesCore.Application;
using SalesCore.Domain;

namespace SalesCore.Integration.Tests;

/// <summary>
/// 月次締めと売掛金の残高(Day58 の完了条件)。残高が売上と入金の履歴から再計算した値と一致し、締めた月の日付の売上・入金が止まる。
/// 規則の定義元は docs/domain/business-rules.md の「4. 請求」の「月次締め」(2026-11-12・11-26 本人の決定)。
/// 月次締めの API と画面は Day59 なので、締めはユースケースを直に呼ぶ(出荷・入金・請求の締めは API で)。
/// </summary>
public sealed class MonthlyClosingTests : ApiTest
{
    public MonthlyClosingTests(DatabaseFixture fixture) : base(fixture)
    {
        Now = new DateTimeOffset(2027, 3, 1, 9, 0, 0, TimeSpan.FromHours(9));
    }

    private MonthlyClosingUseCases Months() => new(Sessions(User), new FixedClock(Now), User!);

    private static (string Code, decimal Opening, decimal Sales, decimal Tax, decimal Received, decimal Closing) Row(ReceivableBalanceView b) =>
        (b.BillingCustomerCode, b.Opening, b.Sales, b.Tax, b.Received, b.Closing);

    // Day57 の10月〜2月(CarryOverApiTests)を月次締めする。支店 C002 の売上は本社 C001 の残高に入る。手で計算した値:
    //   10月: 0 ＋ 1,050 ＋ 税105 − 0 ＝ 1,155(10/31 締めの請求額と同じ)
    //   11月: 1,155 ＋ 210 ＋ 21 − (11/15 の500 ＋ 11/28 の100) ＝ 786(請求額 886 との差は、請求の締めの後に登録した 11/28 の100円)
    //   12月: 786 ＋ 105 ＋ 11 − 2,000 ＝ −1,098(前受。12/31 締めの請求額と同じ)
    //    1月: −1,098 ＋ 返品 −210 ＝ −1,308(返品だけの月は請求書を作らないので税はまだ0。締め忘れとして止めない)
    //    2月: −1,308 ＋ 2,100 ＋ 税189(返品の −21 を含む) ＝ 981(2/28 締めの請求額と同じ)
    [Fact]
    public async Task 十月から二月の残高が手で計算した値になり_履歴から再計算した値と一致する()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var api = Api();
        var octoberOrder = await Order(api, new DateOnly(2026, 10, 1), 12m);
        await Ship(api, octoberOrder.Id, new DateOnly(2026, 10, 10), (octoberOrder.Lines[0].Id, 10m));
        var october = await Close(api, new DateOnly(2026, 10, 31));
        var octoberBalances = await Months().CloseAsync(2026, 10);

        await Pay(api, new DateOnly(2026, 11, 15), 500m);
        var novemberOrder = await Order(api, new DateOnly(2026, 11, 1), 2m);
        await Ship(api, novemberOrder.Id, new DateOnly(2026, 11, 10), (novemberOrder.Lines[0].Id, 2m));
        var november = await Close(api, new DateOnly(2026, 11, 30));
        await Pay(api, new DateOnly(2026, 11, 28), 100m);
        var novemberBalances = await Months().CloseAsync(2026, 11);

        await Pay(api, new DateOnly(2026, 12, 20), 2000m);
        var decemberOrder = await Order(api, new DateOnly(2026, 12, 1), 1m);
        await Ship(api, decemberOrder.Id, new DateOnly(2026, 12, 10), (decemberOrder.Lines[0].Id, 1m));
        var december = await Close(api, new DateOnly(2026, 12, 31));
        var decemberBalances = await Months().CloseAsync(2026, 12);

        var scenario = new TestScenario(Db);
        var loaded = await Db.SalesOrders.Include(o => o.Lines).Include(o => o.Customer).ThenInclude(c => c.BillingCustomer).SingleAsync(o => o.Id == octoberOrder.Id);
        scenario.AddSalesRecord(loaded.RecordReturn(new DateOnly(2027, 1, 10), [new(loaded.Lines[0], 2m)]));
        await scenario.EndRequestAsync();
        var januaryBalances = await Months().CloseAsync(2027, 1);

        var februaryOrder = await Order(api, new DateOnly(2027, 2, 1), 20m);
        await Ship(api, februaryOrder.Id, new DateOnly(2027, 2, 3), (februaryOrder.Lines[0].Id, 20m));
        var february = await Close(api, new DateOnly(2027, 2, 28));
        var februaryBalances = await Months().CloseAsync(2027, 2);

        Assert.Equal(("C001", 0m, 1050m, 105m, 0m, 1155m), Row(Assert.Single(octoberBalances.Balances)));
        Assert.Equal(("C001", 1155m, 210m, 21m, 600m, 786m), Row(Assert.Single(novemberBalances.Balances)));
        Assert.Equal(("C001", 786m, 105m, 11m, 2000m, -1098m), Row(Assert.Single(decemberBalances.Balances)));
        Assert.Equal(("C001", -1098m, -210m, 0m, 0m, -1308m), Row(Assert.Single(januaryBalances.Balances)));
        Assert.Equal(("C001", -1308m, 2100m, 189m, 0m, 981m), Row(Assert.Single(februaryBalances.Balances)));
        // 締め日が月末の請求先は、締めの後に入金を登録しなければ、期末が請求額と同じになる
        Assert.Equal((october.TotalAmount, december.TotalAmount, february.TotalAmount), (1155m, -1098m, 981m));
        Assert.Equal(886m, november.TotalAmount); // 11月の期末 786 ＝ 886 − 請求の締めの後に登録した100円
        Assert.Equal((2027, 2), (februaryBalances.Year, februaryBalances.Month));

        // 履歴から再計算: すべての売上(税抜) ＋ すべての請求書の税 − すべての入金 ＝ 最後の月の期末
        var sales = (await Db.SalesRecords.AsNoTracking().Include(r => r.Lines).ToListAsync()).Aggregate(Money.Zero, (t, r) => t + r.Amount);
        var tax = (await Db.Invoices.AsNoTracking().ToListAsync()).Aggregate(Money.Zero, (t, i) => t + i.TaxAmount);
        var received = (await Db.Receipts.AsNoTracking().ToListAsync()).Aggregate(Money.Zero, (t, r) => t + r.Amount);
        Assert.Equal((3255m, 326m, 2600m), (sales.Yen, tax.Yen, received.Yen));
        Assert.Equal((sales + tax - received).Yen, februaryBalances.Balances[0].Closing);
    }

    // 月次締めの後は、締めた月の日付(末日まで)の出荷の確定・入金・請求の締めを止める。翌月1日の日付は通す。
    // 10月と11月を締めて、一番新しい11月で判定することも確かめる(11月は売上が無いので請求の締めは要らない)
    [Fact]
    public async Task 締めた月の末日の日付の出荷の確定と入金と請求の締めを止め_翌月1日は通す()
    {
        Now = new DateTimeOffset(2026, 12, 31, 9, 0, 0, TimeSpan.FromHours(9));
        await new TestScenario(Db).SeedMastersAsync();
        var api = Api();
        var order = await Order(api, new DateOnly(2026, 10, 1), 12m);
        await Ship(api, order.Id, new DateOnly(2026, 10, 10), (order.Lines[0].Id, 10m));
        await Close(api, new DateOnly(2026, 10, 31));
        await Months().CloseAsync(2026, 10);
        await Months().CloseAsync(2026, 11);
        var instructed = await Send<ShipmentView>(api, $"/sales-orders/{order.Id}/shipments",
            new InstructShipment("W01", [new ShipmentLineRequest(order.Lines[0].Id, 1m)]), HttpStatusCode.Created);

        var shipment = await AssertProblem(api, $"/shipments/{instructed.Id}/confirm", HttpStatusCode.Conflict, new ConfirmShipment(new DateOnly(2026, 11, 30)));
        var receipt = await AssertProblem(api, "/receipts", HttpStatusCode.Conflict, new RegisterReceipt("C001", new DateOnly(2026, 11, 30), 100m));
        var invoice = await AssertProblem(api, "/invoices", HttpStatusCode.Conflict, new CloseInvoice("C001", new DateOnly(2026, 11, 30)));

        Assert.Contains("出荷日 2026-11-30 は、月次締めした2026年11月以前の日付", shipment.Detail);
        Assert.Contains("入金日 2026-11-30 は、月次締めした2026年11月以前の日付", receipt.Detail);
        Assert.Contains("締め日 2026-11-30 は、月次締めした2026年11月以前の日付", invoice.Detail);
        Assert.Equal(new DateOnly(2026, 12, 1), (await Send<ShipmentView>(api, $"/shipments/{instructed.Id}/confirm", new ConfirmShipment(new DateOnly(2026, 12, 1)))).ShippedOn);
        await Pay(api, new DateOnly(2026, 12, 1), 100m);
    }

    // 返品はまだ操作(API・画面)が無いので、DB のトリガーが最後に止める(締めた月の日付の売上)
    [Fact]
    public async Task 締めた月の日付の返品はDBが止める()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var api = Api();
        var order = await Order(api, new DateOnly(2026, 10, 1), 10m);
        await Ship(api, order.Id, new DateOnly(2026, 10, 10), (order.Lines[0].Id, 10m));
        await Close(api, new DateOnly(2026, 10, 31));
        await Months().CloseAsync(2026, 10);
        var scenario = new TestScenario(Db);
        var loaded = await Db.SalesOrders.Include(o => o.Lines).Include(o => o.Customer).ThenInclude(c => c.BillingCustomer).SingleAsync(o => o.Id == order.Id);
        scenario.AddSalesRecord(loaded.RecordReturn(new DateOnly(2026, 10, 31), [new(loaded.Lines[0], 1m)]));

        var error = await Assert.ThrowsAsync<DbUpdateException>(scenario.EndRequestAsync);

        Assert.Equal(new OperationError(409, "月次締めした月の日付になった。読み直してからやり直す。"), OperationError.From(error));
    }

    // 入金日を確かめた後、保存までの間に月次締めが先に済んだ(画面で入金を登録している間に、経理が月次締めした)。
    // ユースケースの確かめは通っているので、保存の時に DB のトリガーが止める
    [Fact]
    public async Task 入金日を確かめた後に月次締めが先に済めば_DBが止める()
    {
        await new TestScenario(Db).SeedMastersAsync();
        await Pay(Api(), new DateOnly(2026, 10, 5), 100m); // 10月が最初の月になる
        var racing = new ReceiptUseCases(new TestSessions(() => CreateAnotherSession(User, new BeforeSave(() => Months().CloseAsync(2026, 10)))), User!, new FixedClock(Now));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => racing.RegisterAsync(new RegisterReceipt("C001", new DateOnly(2026, 10, 20), 200m)));

        Assert.Equal(409, OperationError.From(error)?.Status);
    }

    // 締められるのは終わった月だけで、今日は日本時間で決める。日本時間の 12/1 0時(UTC では 11/30 15時)から11月を締められる
    [Fact]
    public async Task 十一月は日本時間の十二月一日から締められる()
    {
        Now = new DateTimeOffset(2026, 11, 30, 23, 59, 59, TimeSpan.FromHours(9));
        await new TestScenario(Db).SeedMastersAsync();
        await Pay(Api(), new DateOnly(2026, 11, 15), 100m);

        var error = await Assert.ThrowsAsync<InvalidInputException>(() => Months().CloseAsync(2026, 11));
        Now = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.FromHours(9));
        var november = await Months().CloseAsync(2026, 11);

        Assert.Contains("2026年11月はまだ終わっていない", error.Message);
        Assert.Equal(("C001", 0m, 0m, 0m, 100m, -100m), Row(Assert.Single(november.Balances)));
    }

    // 締め忘れの検査には、前の月の日付の未請求の売上も入る(20日締めの C010 の 10/25 の売上は、11/20 締めの請求書に入るはず)
    [Fact]
    public async Task 前の月の未請求の売上を締めていない請求先があれば締めない()
    {
        await new TestScenario(Db).SeedMastersAsync();
        Db.Add(new Customer("C010", "二十日締め", 20));
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        var api = Api();
        var order = await Send<SalesOrderView>(api, "/sales-orders",
            new CreateSalesOrder("C010", new DateOnly(2026, 10, 1), [new CreateSalesOrderLine("P001", 1m)]), HttpStatusCode.Created);
        await Send<SalesOrderView>(api, $"/sales-orders/{order.Id}/approve");
        await Ship(api, order.Id, new DateOnly(2026, 10, 25), (order.Lines[0].Id, 1m));
        await Months().CloseAsync(2026, 10); // 10/25 は 10/20 締めより後なので止めない

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Months().CloseAsync(2026, 11));

        Assert.Contains("C010(2026-11-20 締め)", error.Message);
    }

    // 期末 ＝ 期首 ＋ 売上 ＋ 消費税 − 入金 は DB の検査制約でも止める。トランザクションが失敗の状態になるので最後に
    [Fact]
    public async Task 期末が期首と売上と消費税と入金に合わない残高はDBが止める()
    {
        await new TestScenario(Db).SeedMastersAsync();

        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Db.Database.ExecuteSqlRawAsync("""
            WITH closing AS (INSERT INTO monthly_closings (month, row_version) VALUES ('2026-10-01', 0) RETURNING id)
            INSERT INTO receivable_balances (monthly_closing_id, billing_customer_id, opening, sales, tax, received, closing, row_version)
            SELECT closing.id, customers.id, 0, 1000, 100, 0, 1000, 0 FROM closing, customers WHERE customers.code = 'C001'
            """));

        Assert.Equal("ck_receivable_balances_closing", error.ConstraintName);
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
