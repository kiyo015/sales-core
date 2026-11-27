using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using SalesCore.Application;

namespace SalesCore.Integration.Tests;

/// <summary>
/// 月次締めの API と売掛金の残高の一覧(Day59 の完了条件: 月次締めができ、残高の一覧が出る)。
/// 一覧は締めた月だけ、月を選んで請求先ごと、請求先を選んで月ごとの推移(2026-11-27 本人の決定)。参照は全員、締めは経理と管理者。
/// 今日は日本時間の 2026-12-01 0:30(UTC ではまだ 11/30)。11月が終わったかを UTC で決める誤りが見えるように。
/// </summary>
public sealed class MonthlyClosingApiTests : ApiTest
{
    public MonthlyClosingApiTests(DatabaseFixture fixture) : base(fixture)
    {
        Now = new DateTimeOffset(2026, 12, 1, 0, 30, 0, TimeSpan.FromHours(9));
    }

    // 受注・出荷・請求の締め・入金は管理者が入れ、月次締めは経理がする
    private HttpClient Accounting()
    {
        User = TestUser.With(Roles.Accounting);
        return Api();
    }

    private static (string Code, string Name, decimal Opening, decimal Sales, decimal Tax, decimal Received, decimal Closing) Row(ReceivableBalanceView b) =>
        (b.BillingCustomerCode, b.BillingCustomerName, b.Opening, b.Sales, b.Tax, b.Received, b.Closing);

    private static (int Year, int Month, decimal Opening, decimal Sales, decimal Tax, decimal Received, decimal Closing) Row(MonthlyBalanceView b) =>
        (b.Year, b.Month, b.Opening, b.Sales, b.Tax, b.Received, b.Closing);

    // 10月: 本社 C001 は支店の10箱 1,050 ＋ 税105 − 10/20 の300 ＝ 855、別の会社 C003 は1箱 105 ＋ 税11 ＝ 116。合計 971
    // 11月: 本社は 855 ＋ 2箱 210 ＋ 税21 − 11/1 の500 ＝ 586、別の会社は期首だけ 116。合計 702
    [Fact]
    public async Task 経理が次の月を順に締めると_締めた月の一覧と月の請求先ごとの残高と請求先の推移が出る()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var admin = Api();
        await Sell(admin, "C002", new DateOnly(2026, 10, 10), 10m);
        await Sell(admin, "C003", new DateOnly(2026, 10, 15), 1m);
        await Bill(admin, "C001", new DateOnly(2026, 10, 31));
        await Bill(admin, "C003", new DateOnly(2026, 10, 31));
        await Send<ReceiptView>(admin, "/receipts", new RegisterReceipt("C001", new DateOnly(2026, 10, 20), 300m), HttpStatusCode.Created);
        var api = Accounting();
        Assert.Equal(new NextClosingView(2026, 10, true), await Get<NextClosingView>(api, "/monthly-closings/next")); // 取引の一番古い月

        var response = await api.PostAsJsonAsync("/monthly-closings", new CloseMonth(2026, 10));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/monthly-closings/2026/10", response.Headers.Location?.OriginalString);
        var october = (await response.Content.ReadFromJsonAsync<MonthlyClosingView>())!;
        Assert.Equal([("C001", "本社", 0m, 1050m, 105m, 300m, 855m), ("C003", "別の会社", 0m, 105m, 11m, 0m, 116m)], october.Balances.Select(Row));
        // 11月は日本時間では終わっている(UTC ではまだ 11/30)
        Assert.Equal(new NextClosingView(2026, 11, true), await Get<NextClosingView>(api, "/monthly-closings/next"));

        await Sell(admin, "C002", new DateOnly(2026, 11, 1), 2m);
        await Bill(admin, "C001", new DateOnly(2026, 11, 30));
        await Send<ReceiptView>(admin, "/receipts", new RegisterReceipt("C001", new DateOnly(2026, 11, 1), 500m), HttpStatusCode.Created);
        await Send<MonthlyClosingView>(api, "/monthly-closings", new CloseMonth(2026, 11), HttpStatusCode.Created);

        Assert.Equal(new NextClosingView(2026, 12, false), await Get<NextClosingView>(api, "/monthly-closings/next")); // 12月はまだ終わっていない
        Assert.Equal([new MonthlyClosingSummary(2026, 11, 702m), new MonthlyClosingSummary(2026, 10, 971m)], await Get<List<MonthlyClosingSummary>>(api, "/monthly-closings")); // 新しい順
        var november = await Get<MonthlyClosingView>(api, "/monthly-closings/2026/11");
        Assert.Equal((2026, 11), (november.Year, november.Month));
        Assert.Equal([("C001", "本社", 855m, 210m, 21m, 500m, 586m), ("C003", "別の会社", 116m, 0m, 0m, 0m, 116m)], november.Balances.Select(Row));
        Assert.Equal(october, await Get<MonthlyClosingView>(api, "/monthly-closings/2026/10"), new BalancesComparer());
        var history = await Get<ReceivableHistoryView>(api, "/receivable-balances/C001");
        Assert.Equal(("C001", "本社"), (history.BillingCustomerCode, history.BillingCustomerName));
        Assert.Equal([(2026, 10, 0m, 1050m, 105m, 300m, 855m), (2026, 11, 855m, 210m, 21m, 500m, 586m)], history.Months.Select(Row)); // 古い順
    }

    // 参照は全員(営業も)、締めと「次に締める月」は経理と管理者だけ(業務ルール集「役割」)
    [Fact]
    public async Task 営業は残高を見られるが_締めと次に締める月は403()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var admin = Api();
        await Sell(admin, "C003", new DateOnly(2026, 10, 15), 1m);
        await Bill(admin, "C003", new DateOnly(2026, 10, 31));
        await Send<MonthlyClosingView>(Accounting(), "/monthly-closings", new CloseMonth(2026, 10), HttpStatusCode.Created);
        User = TestUser.With(Roles.Sales);
        var api = Api();

        Assert.Single(await Get<List<MonthlyClosingSummary>>(api, "/monthly-closings"));
        Assert.Single((await Get<MonthlyClosingView>(api, "/monthly-closings/2026/10")).Balances);
        Assert.Single((await Get<ReceivableHistoryView>(api, "/receivable-balances/C003")).Months);
        await AssertProblem(api, "/monthly-closings", HttpStatusCode.Forbidden, new CloseMonth(2026, 11));
        Assert.Equal(HttpStatusCode.Forbidden, (await api.GetAsync("/monthly-closings/next")).StatusCode);
    }

    // 締めていない月は出さない(2026-11-27 本人の決定)。請求先でない得意先(支店)と、無い請求先も 404
    [Fact]
    public async Task 締めていない月と_請求先でない得意先は404()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var api = Accounting();

        Assert.Contains("2026年11月は月次締めしていない", await GetProblem(api, "/monthly-closings/2026/11", HttpStatusCode.NotFound));
        Assert.Contains("C002", await GetProblem(api, "/receivable-balances/C002", HttpStatusCode.NotFound));
        Assert.Contains("NO-SUCH", await GetProblem(api, "/receivable-balances/NO-SUCH", HttpStatusCode.NotFound));
        Assert.Empty(await Get<List<MonthlyClosingSummary>>(api, "/monthly-closings"));
    }

    // 末日の当日(日本時間 11/30 23:59)は、その月はまだ終わっていない。次の日の 0:30 は上のテストで終わっている
    [Fact]
    public async Task 末日の当日は_次に締める月がまだ終わっていない()
    {
        await new TestScenario(Db).SeedMastersAsync();
        Now = new DateTimeOffset(2026, 11, 30, 23, 59, 0, TimeSpan.FromHours(9));
        await Sell(Api(), "C003", new DateOnly(2026, 11, 1), 1m);

        Assert.Equal(new NextClosingView(2026, 11, false), await Get<NextClosingView>(Accounting(), "/monthly-closings/next"));
    }

    // 締められない理由(終わっていない月)は、Domain の理由のまま 400 で返す
    [Fact]
    public async Task 終わっていない月を締めようとすると_理由をつけて400()
    {
        await new TestScenario(Db).SeedMastersAsync();
        await Sell(Api(), "C003", new DateOnly(2026, 12, 1), 1m);

        var problem = await AssertProblem(Accounting(), "/monthly-closings", HttpStatusCode.BadRequest, new CloseMonth(2026, 12));

        Assert.Contains("まだ終わっていない", problem.Detail);
    }

    private static async Task Sell(HttpClient api, string customer, DateOnly on, decimal boxes)
    {
        var order = await Send<SalesOrderView>(api, "/sales-orders", new CreateSalesOrder(customer, on, [new CreateSalesOrderLine("P001", boxes)]), HttpStatusCode.Created);
        await Send<SalesOrderView>(api, $"/sales-orders/{order.Id}/approve");
        await Ship(api, order.Id, on, (order.Lines[0].Id, boxes));
    }

    private static Task<InvoiceView> Bill(HttpClient api, string billingCustomer, DateOnly closingDate) =>
        Send<InvoiceView>(api, "/invoices", new CloseInvoice(billingCustomer, closingDate), HttpStatusCode.Created);

    private static async Task<string> GetProblem(HttpClient api, string path, HttpStatusCode expected)
    {
        var response = await api.GetAsync(path);
        Assert.Equal(expected, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Detail!;
    }

    // 残高の行(リスト)は record の等しさで比べられないので、中身で比べる
    private sealed class BalancesComparer : IEqualityComparer<MonthlyClosingView>
    {
        public bool Equals(MonthlyClosingView? x, MonthlyClosingView? y) =>
            (x!.Year, x.Month) == (y!.Year, y.Month) && x.Balances.SequenceEqual(y.Balances);

        public int GetHashCode(MonthlyClosingView obj) => HashCode.Combine(obj.Year, obj.Month);
    }
}
