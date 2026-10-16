using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SalesCore.Application;
using SalesCore.Infrastructure.Persistence;

namespace SalesCore.Integration.Tests;

/// <summary>
/// API を HTTP 経由で呼ぶテストの基底クラス。API の DbContext はテストのトランザクションを共有するので、
/// API が書いたデータもテストの終わりに取り消される。
/// </summary>
public abstract class ApiTest(DatabaseFixture fixture) : DatabaseTest(fixture), IDisposable
{
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    // context を渡すと、API はその DbContext を使う(同時更新や例外の再現用)。省くと、リクエストごとに新しい2人目を作る
    protected HttpClient Api(Func<SalesCoreDbContext>? context = null)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            // Development でないので user-secrets(開発用DB)は読まない。接続はテストのトランザクションに差し替える
            web.UseEnvironment("Testing");
            web.UseSetting("ConnectionStrings:SalesCore", Fixture.ConnectionString);
            web.ConfigureTestServices(services => services.AddScoped(_ => context?.Invoke() ?? CreateAnotherSession()));
        });
        _factories.Add(factory);
        return factory.CreateClient();
    }

    /// <summary>支店(C002、請求先は本社)の受注を登録する。</summary>
    protected static async Task<SalesOrderView> CreateOrder(HttpClient api, params CreateSalesOrderLine[] lines) =>
        await CreateOrder(api, new DateOnly(2026, 10, 1), lines);

    protected static async Task<SalesOrderView> CreateOrder(HttpClient api, DateOnly orderedOn, params CreateSalesOrderLine[] lines) =>
        await Send<SalesOrderView>(api, "/sales-orders", new CreateSalesOrder("C002", orderedOn, lines), HttpStatusCode.Created);

    /// <summary>出荷を指示して、確定する(売上が計上される)。</summary>
    protected static async Task<ShipmentView> Ship(HttpClient api, long orderId, DateOnly shippedOn, params (long Line, decimal Quantity)[] lines)
    {
        var instructed = await Send<ShipmentView>(api, $"/sales-orders/{orderId}/shipments",
            new InstructShipment("W01", lines.Select(l => new ShipmentLineRequest(l.Line, l.Quantity)).ToList()), HttpStatusCode.Created);
        return await Send<ShipmentView>(api, $"/shipments/{instructed.Id}/confirm", new ConfirmShipment(shippedOn));
    }

    protected static async Task<T> Get<T>(HttpClient api, string path) => (await api.GetFromJsonAsync<T>(path))!;

    protected static async Task<T> Send<T>(HttpClient api, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await api.PostAsJsonAsync(path, body);
        Assert.True(response.StatusCode == expected, $"{path}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    protected static async Task<ProblemDetails> AssertProblem(HttpClient api, string path, HttpStatusCode expected, object? body = null)
    {
        var response = await api.PostAsJsonAsync(path, body);
        Assert.Equal(expected, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>())!;
        Assert.False(string.IsNullOrEmpty(problem.Detail));
        return problem;
    }

    public void Dispose() => _factories.ForEach(f => f.Dispose());
}
