using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SalesCore.Application;
using SalesCore.Domain;

namespace SalesCore.Integration.Tests;

/// <summary>
/// 締め請求書の API と、受注から請求書までを API だけで通すテスト(Day31 の完了条件)。
/// 規則の定義元は docs/domain/business-rules.md の「2. 消費税」「4. 請求」。
/// </summary>
public sealed class InvoiceApiTests(DatabaseFixture fixture) : ApiTest(fixture)
{
    // 支店(請求先は本社・月末締め)の受注。ボルト(105円・10%)3箱とお茶(33.33円・8%)3本を、10月に3回に分けて出す。
    // 10月末で締めると、本社あてに10月の3回分の売上で請求書ができる。11月の出荷は11月末の請求書に入る
    [Fact]
    public async Task 受注から分納での出荷を経て_月末に締めると_請求先に税率ごとに1回だけ丸めた請求書ができる()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var api = Api();
        var order = await CreateOrder(api, new CreateSalesOrderLine("P001", 3m), new CreateSalesOrderLine("P002", 3m));
        await Send<SalesOrderView>(api, $"/sales-orders/{order.Id}/approve");
        var (bolt, tea) = (order.Lines[0].Id, order.Lines[1].Id);

        var first = await Ship(api, order.Id, new DateOnly(2026, 10, 5), (bolt, 1m), (tea, 2m));   // 105 + Round(66.66)=67
        var second = await Ship(api, order.Id, new DateOnly(2026, 10, 12), (bolt, 1m));            // 105
        var third = await Ship(api, order.Id, new DateOnly(2026, 10, 19), (bolt, 1m), (tea, 1m));  // 105 + (Round(99.99) − 67)=33
        Assert.Equal("Shipped", (await Get<SalesOrderView>(api, $"/sales-orders/{order.Id}")).Status);

        // 11月の受注・出荷(10月の請求書には入らない)
        var november = await CreateOrder(api, new DateOnly(2026, 11, 1), new CreateSalesOrderLine("P001", 1m));
        await Send<SalesOrderView>(api, $"/sales-orders/{november.Id}/approve");
        var novemberShipment = await Ship(api, november.Id, new DateOnly(2026, 11, 2), (november.Lines[0].Id, 1m));

        var october = await Send<InvoiceView>(api, "/invoices", new CloseInvoice("C001", new DateOnly(2026, 10, 31)), HttpStatusCode.Created);

        Assert.Matches(@"^IN-\d{6}$", october.Number);
        Assert.Equal(("C001", "Confirmed"), (october.BillingCustomerCode, october.Status));
        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)), (october.PeriodFrom, october.PeriodTo));
        Assert.Equal(
            new[] { first.SalesRecord!.Number, second.SalesRecord!.Number, third.SalesRecord!.Number }.Order(),
            october.SalesRecordNumbers.Order());
        // 税率ごとに1回だけ丸める(2週目の TaxCalculatorTests と同じ規則)。10%: 315 × 10% = 31.5 → 32、8%: 100 × 8% = 8。
        // 売上ごとに丸めると 11 + 5 + 11 + 11 + 3 = 41円になり、1円ずれる
        Assert.Equal([new InvoiceTaxSummaryView(10m, 315m, 32m), new InvoiceTaxSummaryView(8m, 100m, 8m)], october.TaxSummaries);
        Assert.Equal((415m, 40m, 455m), (october.SalesAmount, october.TaxAmount, october.TotalAmount));
        Assert.Equal((0m, 0m, 0m), (october.PreviousBalance, october.ReceivedAmount, october.CarriedOver)); // 段階1は繰越しない
        Assert.Null(october.DueDate);
        Assert.Equivalent(october, await Get<InvoiceView>(api, $"/invoices/{october.Id}"), strict: true); // 照会しても同じ中身

        // 同じ売上は二重に請求しない: 10月はもう締められず、11月の請求書には11月の売上だけが入る
        await AssertProblem(api, "/invoices", HttpStatusCode.Conflict, new CloseInvoice("C001", new DateOnly(2026, 10, 31)));
        var nov = await Send<InvoiceView>(api, "/invoices", new CloseInvoice("C001", new DateOnly(2026, 11, 30)), HttpStatusCode.Created);
        Assert.Equal([novemberShipment.SalesRecord!.Number], nov.SalesRecordNumbers);
        Assert.Equal((new DateOnly(2026, 11, 1), 105m, 11m, 116m), (nov.PeriodFrom, nov.SalesAmount, nov.TaxAmount, nov.TotalAmount));
    }

    // 締め日までの未請求の売上は全部入れる(2026-10-16 決定)。前の期間に入れ忘れた売上も、次の締めで拾う
    [Fact]
    public async Task 期間より前の未請求の売上も_次の締めで請求する()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var api = Api();
        var order = await CreateOrder(api, new DateOnly(2026, 9, 1), new CreateSalesOrderLine("P001", 1m));
        await Send<SalesOrderView>(api, $"/sales-orders/{order.Id}/approve");
        var september = await Ship(api, order.Id, new DateOnly(2026, 9, 20), (order.Lines[0].Id, 1m));

        var invoice = await Send<InvoiceView>(api, "/invoices", new CloseInvoice("C001", new DateOnly(2026, 10, 31)), HttpStatusCode.Created);

        Assert.Equal([september.SalesRecord!.Number], invoice.SalesRecordNumbers);
        Assert.Equal(new DateOnly(2026, 10, 1), invoice.PeriodFrom); // 期間は前回の締め日(9/30)の翌日から
    }

    // 請求書は請求先ごと。支店(C002)の売上は本社(C001)に、別の会社(C003)の売上は C003 に入る
    [Fact]
    public async Task 別の請求先の売上は入らない()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var api = Api();
        var branch = await CreateOrder(api, new CreateSalesOrderLine("P001", 1m));
        var other = await Send<SalesOrderView>(api, "/sales-orders",
            new CreateSalesOrder("C003", new DateOnly(2026, 10, 1), [new CreateSalesOrderLine("P002", 3m)]), HttpStatusCode.Created);
        foreach (var order in new[] { branch, other })
        {
            await Send<SalesOrderView>(api, $"/sales-orders/{order.Id}/approve");
        }
        var branchShipment = await Ship(api, branch.Id, new DateOnly(2026, 10, 5), (branch.Lines[0].Id, 1m));
        var otherShipment = await Ship(api, other.Id, new DateOnly(2026, 10, 5), (other.Lines[0].Id, 3m));

        var headOffice = await Send<InvoiceView>(api, "/invoices", new CloseInvoice("C001", new DateOnly(2026, 10, 31)), HttpStatusCode.Created);
        var otherCompany = await Send<InvoiceView>(api, "/invoices", new CloseInvoice("C003", new DateOnly(2026, 10, 31)), HttpStatusCode.Created);

        Assert.Equal([branchShipment.SalesRecord!.Number], headOffice.SalesRecordNumbers);
        Assert.Equal([otherShipment.SalesRecord!.Number], otherCompany.SalesRecordNumbers);
        Assert.Equal((105m, 100m), (headOffice.SalesAmount, otherCompany.SalesAmount));
    }

    [Fact]
    public async Task 締めの入力の誤りは400で_請求する売上が無ければ409()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var api = Api();

        // 月末締めの得意先に 10/30 締め
        await AssertProblem(api, "/invoices", HttpStatusCode.BadRequest, new CloseInvoice("C001", new DateOnly(2026, 10, 30)));
        // 支店は請求先ではない(請求書は本社 C001 に送る)
        var branch = await AssertProblem(api, "/invoices", HttpStatusCode.BadRequest, new CloseInvoice("C002", new DateOnly(2026, 10, 31)));
        Assert.Contains("C001", branch.Detail);
        await AssertProblem(api, "/invoices", HttpStatusCode.BadRequest, new CloseInvoice("NOPE", new DateOnly(2026, 10, 31)));
        // 売上の無い得意先
        await AssertProblem(api, "/invoices", HttpStatusCode.Conflict, new CloseInvoice("C003", new DateOnly(2026, 10, 31)));

        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync("/invoices/999999999")).StatusCode);
    }

    // 締め日が来るまでは締められない(2026-10-16 の AIレビュー)。段階1は請求書を取り消せないので、
    // 未来の日付で締めると、その日までの締めがすべてできなくなる。締め日の当日からは締められる
    [Fact]
    public async Task 締め日が来るまでは締められない()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var order = await CreateOrder(Api(), new CreateSalesOrderLine("P001", 1m));
        await Send<SalesOrderView>(Api(), $"/sales-orders/{order.Id}/approve");
        await Ship(Api(), order.Id, new DateOnly(2026, 10, 5), (order.Lines[0].Id, 1m));

        Now = new DateTimeOffset(2026, 10, 30, 23, 59, 0, TimeSpan.FromHours(9)); // 日本時間で 10/30 の夜
        var early = await AssertProblem(Api(), "/invoices", HttpStatusCode.BadRequest, new CloseInvoice("C001", new DateOnly(2026, 10, 31)));
        Assert.Contains("2026-10-31", early.Detail);

        Now = new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.FromHours(9)); // UTC ではまだ 10/30 だが、日本時間では 10/31
        await Send<InvoiceView>(Api(), "/invoices", new CloseInvoice("C001", new DateOnly(2026, 10, 31)), HttpStatusCode.Created);
    }

    // 2人が同時に同じ締め日で締めると、どちらも「まだ締めていない」と見て進み、保存の時に一意インデックスに当たる。
    // 保存の直前に、もう1人の請求書を割り込ませて再現する。500 でなく 409 で、理由を返す(2026-10-16 の AIレビュー)
    [Fact]
    public async Task 同じ締め日で同時に締めると_後の方は409()
    {
        var m = await new TestScenario(Db).SeedMastersAsync();
        var order = await CreateOrder(Api(), new CreateSalesOrderLine("P001", 1m));
        await Send<SalesOrderView>(Api(), $"/sales-orders/{order.Id}/approve");
        await Ship(Api(), order.Id, new DateOnly(2026, 10, 5), (order.Lines[0].Id, 1m));

        var racing = Api(() => CreateAnotherSession(new InsertInvoiceBeforeSave(m.HeadOffice.Id, new DateOnly(2026, 10, 31))));
        var problem = await AssertProblem(racing, "/invoices", HttpStatusCode.Conflict, new CloseInvoice("C001", new DateOnly(2026, 10, 31)));

        Assert.Contains("先に", problem.Detail);
    }

    // 保存の直前に、同じ請求先・締め日の請求書を SQL で入れる(もう1人が先に締めた状態)
    private sealed class InsertInvoiceBeforeSave(long billingCustomerId, DateOnly closingDate) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<Invoice>().Any(e => e.State == EntityState.Added))
            {
                await eventData.Context.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO invoices (closing_date, period_from, period_to, previous_balance, received_amount, carried_over,
                                          sales_amount, tax_amount, total_amount, status, billing_customer_id, row_version)
                    VALUES ({closingDate}, {closingDate.AddDays(-30)}, {closingDate}, 0, 0, 0, 1, 0, 1, 'Confirmed', {billingCustomerId}, 0)
                    """, cancellationToken);
            }
            return result;
        }
    }

    [Fact]
    public async Task 前回の締め日より前には締められない()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var api = Api();
        var order = await CreateOrder(api, new DateOnly(2026, 9, 1), new CreateSalesOrderLine("P001", 2m));
        await Send<SalesOrderView>(api, $"/sales-orders/{order.Id}/approve");
        await Ship(api, order.Id, new DateOnly(2026, 9, 20), (order.Lines[0].Id, 1m));
        await Send<InvoiceView>(api, "/invoices", new CloseInvoice("C001", new DateOnly(2026, 10, 31)), HttpStatusCode.Created);
        await Ship(api, order.Id, new DateOnly(2026, 9, 25), (order.Lines[0].Id, 1m)); // 締めた後に入った9月の売上

        // 9月末はもう締められない(10月末で締めている)。9月の売上は11月末の締めで拾う
        await AssertProblem(api, "/invoices", HttpStatusCode.Conflict, new CloseInvoice("C001", new DateOnly(2026, 9, 30)));
        var november = await Send<InvoiceView>(api, "/invoices", new CloseInvoice("C001", new DateOnly(2026, 11, 30)), HttpStatusCode.Created);
        Assert.Equal(105m, november.SalesAmount);
    }
}
