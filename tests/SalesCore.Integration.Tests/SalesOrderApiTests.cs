using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SalesCore.Application;
using SalesCore.Domain;

namespace SalesCore.Integration.Tests;

/// <summary>
/// 受注・出荷の API を HTTP 経由で呼ぶ。API の DbContext はテストのトランザクションを共有するので、
/// API が書いたデータもテストの終わりに取り消される。
/// </summary>
public sealed class SalesOrderApiTests(DatabaseFixture fixture) : DatabaseTest(fixture), IDisposable
{
    private WebApplicationFactory<Program>? _factory;

    [Fact]
    public async Task 受注を登録して承認し_2回に分けて出荷を確定すると_一部出荷を経て出荷済になり_売上が累計差分で計上される()
    {
        var m = await new TestScenario(Db).SeedMastersAsync();
        var api = Api();

        // 支店の受注。ボルト(105円・10%)3箱、お茶(33.33円・8%)3本。単価は省略 = 商品の標準単価
        var created = await api.PostAsJsonAsync("/sales-orders", new CreateSalesOrder("C002", new DateOnly(2026, 10, 1),
            [new CreateSalesOrderLine("P001", 3m), new CreateSalesOrderLine("P002", 3m)]));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var order = (await created.Content.ReadFromJsonAsync<SalesOrderView>())!;
        Assert.Equal($"/sales-orders/{order.Id}", created.Headers.Location!.OriginalString);
        Assert.Matches(@"^SO-\d{6}$", order.Number);
        Assert.Equal("Received", order.Status);
        Assert.Equal(415m, order.Amount); // 315 + Round(99.99)
        Assert.Equal([(105m, 10m), (33.33m, 8m)], order.Lines.Select(l => (l.UnitPrice, l.TaxRate)));

        Assert.Equal("Approved", (await Send<SalesOrderView>(api, $"/sales-orders/{order.Id}/approve")).Status);

        var bolt = order.Lines[0].Id;
        var tea = order.Lines[1].Id;
        var first = await Ship(api, order.Id, new DateOnly(2026, 10, 5), (bolt, 1m), (tea, 2m));
        Assert.Equal(105m + 67m, first.SalesRecord!.Amount); // お茶は Round(66.66) = 67
        Assert.Matches(@"^SR-\d{6}$", first.SalesRecord.Number);
        Assert.Equal("PartiallyShipped", (await Get<SalesOrderView>(api, $"/sales-orders/{order.Id}")).Status);

        var second = await Ship(api, order.Id, new DateOnly(2026, 10, 12), (bolt, 2m), (tea, 1m));
        Assert.Equal(210m + 33m, second.SalesRecord!.Amount); // お茶は Round(99.99) − 67 = 33
        var shipped = await Get<SalesOrderView>(api, $"/sales-orders/{order.Id}");
        Assert.Equal("Shipped", shipped.Status);
        Assert.All(shipped.Lines, l => Assert.Equal(0m, l.RemainingQuantity));

        // 売上の請求先は、計上した時点の請求先(支店の売上なら本社)
        var billingCustomers = await Db.SalesRecords
            .Where(r => r.Id == first.SalesRecord.Id || r.Id == second.SalesRecord.Id)
            .Select(r => EF.Property<long>(r, "BillingCustomerId"))
            .ToListAsync();
        Assert.Equal([m.HeadOffice.Id, m.HeadOffice.Id], billingCustomers);
    }

    [Fact]
    public async Task 単価を指定すると_標準単価の代わりにその単価で受注する()
    {
        await new TestScenario(Db).SeedMastersAsync();

        var order = await CreateOrder(Api(), new CreateSalesOrderLine("P001", 3m, UnitPrice: 100m));

        Assert.Equal(100m, order.Lines[0].UnitPrice);
        Assert.Equal(300m, order.Amount);
    }

    [Fact]
    public async Task 税率は受注日時点の_商品の税区分の税率()
    {
        await new TestScenario(Db).SeedMastersAsync();
        // 2026-09-30 までは 8%、10-01 から 10% に変わる税区分の商品
        var changing = new TaxCategory("CHG", "改定あり");
        Db.AddRange(
            new TaxRatePeriod(changing, TaxRate.Of(8m), new DateOnly(2019, 10, 1), new DateOnly(2026, 9, 30)),
            new TaxRatePeriod(changing, TaxRate.Of(10m), new DateOnly(2026, 10, 1)),
            new Product("P900", "改定品", "個", changing, 100m));
        await Db.SaveChangesAsync();
        var api = Api();

        var before = await Send<SalesOrderView>(api, "/sales-orders",
            new CreateSalesOrder("C002", new DateOnly(2026, 9, 30), [new CreateSalesOrderLine("P900", 1m)]), HttpStatusCode.Created);
        var after = await Send<SalesOrderView>(api, "/sales-orders",
            new CreateSalesOrder("C002", new DateOnly(2026, 10, 1), [new CreateSalesOrderLine("P900", 1m)]), HttpStatusCode.Created);

        Assert.Equal(8m, before.Lines[0].TaxRate);
        Assert.Equal(10m, after.Lines[0].TaxRate);
        // 税率の無い日(2019-10-01 より前)は受け付けない
        await AssertProblem(api, "/sales-orders", HttpStatusCode.BadRequest,
            new CreateSalesOrder("C002", new DateOnly(2019, 9, 30), [new CreateSalesOrderLine("P900", 1m)]));
    }

    [Fact]
    public async Task 番号はDBが振り_受注ごとに違う()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var api = Api();

        var a = await CreateOrder(api, new CreateSalesOrderLine("P001", 1m));
        var b = await CreateOrder(api, new CreateSalesOrderLine("P001", 1m));

        Assert.NotEqual(a.Number, b.Number);
    }

    [Fact]
    public async Task 状態に合わない操作は409で_状態は変わらない()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var api = Api();
        var order = await CreateOrder(api, new CreateSalesOrderLine("P001", 3m));
        var line = order.Lines[0].Id;

        // 承認前の出荷
        await AssertProblem(api, $"/sales-orders/{order.Id}/shipments", HttpStatusCode.Conflict,
            new InstructShipment("W01", [new ShipmentLineRequest(line, 1m)]));
        await Send<SalesOrderView>(api, $"/sales-orders/{order.Id}/approve");
        // 2回目の承認
        await AssertProblem(api, $"/sales-orders/{order.Id}/approve", HttpStatusCode.Conflict);
        // 受注数量を超える出荷
        await AssertProblem(api, $"/sales-orders/{order.Id}/shipments", HttpStatusCode.Conflict,
            new InstructShipment("W01", [new ShipmentLineRequest(line, 4m)]));

        var shipment = await Ship(api, order.Id, new DateOnly(2026, 10, 5), (line, 1m));
        // 確定済みの出荷をもう一度確定・取消、一部出荷の受注の取消
        await AssertProblem(api, $"/shipments/{shipment.Id}/confirm", HttpStatusCode.Conflict, new ConfirmShipment(new DateOnly(2026, 10, 6)));
        await AssertProblem(api, $"/shipments/{shipment.Id}/cancel", HttpStatusCode.Conflict);
        await AssertProblem(api, $"/sales-orders/{order.Id}/cancel", HttpStatusCode.Conflict);

        Assert.Equal("PartiallyShipped", (await Get<SalesOrderView>(api, $"/sales-orders/{order.Id}")).Status);
        Assert.Equal("Confirmed", (await Get<ShipmentView>(api, $"/shipments/{shipment.Id}")).Status);
    }

    [Fact]
    public async Task 取り消した受注は承認できず_指示だけの出荷は取り消せる()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var api = Api();

        var cancelled = await CreateOrder(api, new CreateSalesOrderLine("P001", 1m));
        Assert.Equal("Cancelled", (await Send<SalesOrderView>(api, $"/sales-orders/{cancelled.Id}/cancel")).Status);
        await AssertProblem(api, $"/sales-orders/{cancelled.Id}/approve", HttpStatusCode.Conflict);

        var order = await CreateOrder(api, new CreateSalesOrderLine("P001", 2m));
        await Send<SalesOrderView>(api, $"/sales-orders/{order.Id}/approve");
        var instructed = await Send<ShipmentView>(api, $"/sales-orders/{order.Id}/shipments",
            new InstructShipment("W01", [new ShipmentLineRequest(order.Lines[0].Id, 2m)]), HttpStatusCode.Created);
        Assert.Equal("Instructed", instructed.Status);
        Assert.Matches(@"^SH-\d{6}$", instructed.Number);

        Assert.Equal("Cancelled", (await Send<ShipmentView>(api, $"/shipments/{instructed.Id}/cancel")).Status);
        var after = await Get<SalesOrderView>(api, $"/sales-orders/{order.Id}");
        Assert.Equal("Approved", after.Status); // 指示を取り消しても受注は動かない
        Assert.Equal(0m, after.Lines[0].ShippedQuantity);
    }

    [Fact]
    public async Task 入力の誤りは400_無いものは404で_理由を返す()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var api = Api();
        var date = new DateOnly(2026, 10, 1);

        var unknownProduct = await AssertProblem(api, "/sales-orders", HttpStatusCode.BadRequest,
            new CreateSalesOrder("C002", date, [new CreateSalesOrderLine("NOPE", 1m)]));
        Assert.Contains("NOPE", unknownProduct.Detail);
        await AssertProblem(api, "/sales-orders", HttpStatusCode.BadRequest,
            new CreateSalesOrder("NOPE", date, [new CreateSalesOrderLine("P001", 1m)]));
        await AssertProblem(api, "/sales-orders", HttpStatusCode.BadRequest,
            new CreateSalesOrder("C002", date, [new CreateSalesOrderLine("P001", 0m)]));
        await AssertProblem(api, "/sales-orders", HttpStatusCode.BadRequest, new CreateSalesOrder("C002", date, []));

        var order = await CreateOrder(api, new CreateSalesOrderLine("P001", 1m));
        await Send<SalesOrderView>(api, $"/sales-orders/{order.Id}/approve");
        await AssertProblem(api, $"/sales-orders/{order.Id}/shipments", HttpStatusCode.BadRequest,
            new InstructShipment("NOPE", [new ShipmentLineRequest(order.Lines[0].Id, 1m)]));
        await AssertProblem(api, $"/sales-orders/{order.Id}/shipments", HttpStatusCode.BadRequest,
            new InstructShipment("W01", [new ShipmentLineRequest(-1, 1m)])); // この受注の明細ではない

        var missing = await api.GetAsync("/sales-orders/999999999");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        await AssertProblem(api, "/shipments/999999999/confirm", HttpStatusCode.NotFound, new ConfirmShipment(date));
    }

    // マスタは削除せず無効にする(業務ルール集「7. データの扱い」)。無効にする操作は段階1に無いので、SQL で無効にする
    [Fact]
    public async Task 無効にした得意先_商品_倉庫では受注も出荷もできない()
    {
        var m = await new TestScenario(Db).SeedMastersAsync();
        var api = Api();
        var order = await CreateOrder(api, new CreateSalesOrderLine("P001", 1m));
        await Send<SalesOrderView>(api, $"/sales-orders/{order.Id}/approve");

        await Db.Database.ExecuteSqlAsync($"UPDATE customers SET is_active = false WHERE id = {m.Branch.Id}");
        await Db.Database.ExecuteSqlAsync($"UPDATE products SET is_active = false WHERE id = {m.ReducedProduct.Id}");
        await Db.Database.ExecuteSqlAsync($"UPDATE warehouses SET is_active = false WHERE id = {m.Warehouse.Id}");

        await AssertProblem(api, "/sales-orders", HttpStatusCode.BadRequest,
            new CreateSalesOrder("C002", new DateOnly(2026, 10, 1), [new CreateSalesOrderLine("P001", 1m)]));
        await AssertProblem(api, "/sales-orders", HttpStatusCode.BadRequest,
            new CreateSalesOrder("C001", new DateOnly(2026, 10, 1), [new CreateSalesOrderLine("P002", 1m)]));
        await AssertProblem(api, $"/sales-orders/{order.Id}/shipments", HttpStatusCode.BadRequest,
            new InstructShipment("W01", [new ShipmentLineRequest(order.Lines[0].Id, 1m)]));
    }

    private HttpClient Api()
    {
        _factory ??= new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            // Development でないので user-secrets(開発用DB)は読まない。接続はテストのトランザクションに差し替える
            web.UseEnvironment("Testing");
            web.UseSetting("ConnectionStrings:SalesCore", Fixture.ConnectionString);
            web.ConfigureTestServices(services => services.AddScoped(_ => CreateAnotherSession()));
        });
        return _factory.CreateClient();
    }

    private static async Task<SalesOrderView> CreateOrder(HttpClient api, params CreateSalesOrderLine[] lines) =>
        await Send<SalesOrderView>(api, "/sales-orders", new CreateSalesOrder("C002", new DateOnly(2026, 10, 1), lines), HttpStatusCode.Created);

    private static async Task<ShipmentView> Ship(HttpClient api, long orderId, DateOnly shippedOn, params (long Line, decimal Quantity)[] lines)
    {
        var instructed = await Send<ShipmentView>(api, $"/sales-orders/{orderId}/shipments",
            new InstructShipment("W01", lines.Select(l => new ShipmentLineRequest(l.Line, l.Quantity)).ToList()), HttpStatusCode.Created);
        return await Send<ShipmentView>(api, $"/shipments/{instructed.Id}/confirm", new ConfirmShipment(shippedOn));
    }

    private static async Task<T> Get<T>(HttpClient api, string path) => (await api.GetFromJsonAsync<T>(path))!;

    private static async Task<T> Send<T>(HttpClient api, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await api.PostAsJsonAsync(path, body);
        Assert.True(response.StatusCode == expected, $"{path}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<ProblemDetails> AssertProblem(HttpClient api, string path, HttpStatusCode expected, object? body = null)
    {
        var response = await api.PostAsJsonAsync(path, body);
        Assert.Equal(expected, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>())!;
        Assert.False(string.IsNullOrEmpty(problem.Detail));
        return problem;
    }

    public void Dispose() => _factory?.Dispose();
}
