using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SalesCore.Application;
using SalesCore.Infrastructure.Persistence;
using SalesCore.Infrastructure.Persistence.Identity;

namespace SalesCore.Integration.Tests;

/// <summary>
/// API を HTTP 経由で呼ぶテストの基底クラス。API の DbContext はテストのトランザクションを共有するので、
/// API が書いたデータもテストの終わりに取り消される。
/// </summary>
public abstract class ApiTest(DatabaseFixture fixture) : DatabaseTest(fixture), IDisposable
{
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    /// <summary>API の「今」。テストの日付(2026年10〜11月)の締めがすべてできるように、既定は 2026-12-31(日本時間)。</summary>
    protected DateTimeOffset Now { get; set; } = new(2026, 12, 31, 12, 0, 0, TimeSpan.FromHours(9));

    /// <summary>
    /// API を呼ぶ利用者。既定は管理者(業務の流れのテストで、全部の操作ができる)。
    /// null にすると差し替えず、本当のログイン(<see cref="LoginAsync"/> で受け取るトークン)で呼ぶ。
    /// </summary>
    protected TestUser? User { get; set; } = TestUser.With(Roles.Admin);

    /// <summary>最後に作った API の時計。進めると、トークンの有効期限が切れた後を再現できる。</summary>
    protected FixedClock Clock { get; private set; } = null!;

    // context を渡すと、API はその DbContext を使う(同時更新や例外の再現用)。省くと、リクエストごとに新しい2人目を作る
    protected HttpClient Api(Func<SalesCoreDbContext>? context = null)
    {
        var clock = Clock = new FixedClock(Now);
        var user = User;
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            // Development でないので user-secrets(開発用DB)は読まない。接続はテストのトランザクションに差し替える
            web.UseEnvironment("Testing");
            web.UseSetting("ConnectionStrings:SalesCore", Fixture.ConnectionString);
            web.ConfigureTestServices(services =>
            {
                services.AddScoped(provider => context?.Invoke() ?? CreateAnotherSession(provider.GetRequiredService<ICurrentUserSource>()));
                services.AddScoped(_ => CreateIdentitySession());
                services.AddSingleton<TimeProvider>(clock);
                if (user is not null) services.AddScoped<ICurrentUser>(_ => user);
            });
        });
        _factories.Add(factory);
        return factory.CreateClient();
    }

    /// <summary>最後に作った API の中で、役割を付けた利用者を作る(画面の起動時と同じ AccountUseCases)。</summary>
    protected async Task CreateUserAsync(string userName, string password, params string[] roles)
    {
        using var scope = _factories[^1].Services.CreateScope();
        var account = scope.ServiceProvider.GetRequiredService<AccountUseCases>();
        await account.EnsureRolesAsync();
        Assert.True(await account.EnsureUserAsync(userName, userName, password, roles));
    }

    /// <summary>API にログインする。成功すれば、以降の呼び出しにトークンを付ける。</summary>
    protected static async Task<HttpResponseMessage> LoginAsync(HttpClient api, string userName, string password)
    {
        var response = await api.PostAsJsonAsync("/account/login", new { userName, password });
        if (response.IsSuccessStatusCode)
        {
            // 文字列で読む(ストリームで読むと閉じられ、呼んだ側で中身を読めなくなる)
            var token = JsonSerializer.Deserialize<AccessToken>(await response.Content.ReadAsStringAsync())!;
            api.DefaultRequestHeaders.Authorization = new("Bearer", token.Value);
        }
        return response;
    }

    /// <summary>ログインの応答(Identity の Bearer トークン)。</summary>
    protected sealed record AccessToken(
        [property: JsonPropertyName("tokenType")] string TokenType,
        [property: JsonPropertyName("accessToken")] string Value,
        [property: JsonPropertyName("expiresIn")] long ExpiresIn);

    // 利用者と役割(Identity)の DbContext も、このテストのトランザクションに入れる
    private SalesCoreIdentityDbContext CreateIdentitySession()
    {
        var identity = new SalesCoreIdentityDbContext(
            new DbContextOptionsBuilder<SalesCoreIdentityDbContext>().UseNpgsql(Db.Database.GetDbConnection()).Options);
        JoinTransaction(identity);
        return identity;
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
