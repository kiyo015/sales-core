using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesCore.Application;

namespace SalesCore.Integration.Tests;

/// <summary>
/// API のログイン(業務ルール集「8. 利用者とログイン」)と、権限の実効性(段階2の設計「確かめ方」)。
/// テストの利用者に差し替えず、画面と同じ利用者でログインしてトークンを受け取り、それを付けて呼ぶ。
/// トークンは Identity の Bearer で、有効期限は1時間。切れたらログインし直す(更新の口は作らない。本人の決定 2026-10-28)。
/// </summary>
public sealed class ApiAuthenticationTests : ApiTest
{
    private const string Password = "correct horse battery"; // テスト用の値(12文字以上)

    public ApiAuthenticationTests(DatabaseFixture fixture) : base(fixture) => User = null;

    [Fact]
    public async Task 正しいパスワードでログインすると_1時間のトークンを受け取る()
    {
        var api = Api();
        await CreateUserAsync("sato", Password, Roles.Sales);

        var response = await LoginAsync(api, "sato", Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = (await response.Content.ReadFromJsonAsync<AccessToken>())!;
        Assert.Equal("Bearer", token.TokenType);
        Assert.False(string.IsNullOrEmpty(token.Value));
        Assert.Equal(3600, token.ExpiresIn);
    }

    // 利用者が無いのか、パスワードが違うのかは区別しない(画面と同じ)
    [Fact]
    public async Task パスワードが違っても_利用者が無くても_同じ401()
    {
        var api = Api();
        await CreateUserAsync("sato", Password, Roles.Sales);

        var wrongPassword = await LoginAsync(api, "sato", "wrong password!!");
        var noSuchUser = await LoginAsync(api, "nobody", Password);

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, noSuchUser.StatusCode);
        Assert.Equal(await DetailOf(wrongPassword), await DetailOf(noSuchUser));
        Assert.Null(api.DefaultRequestHeaders.Authorization);
    }

    // API は SignInManager の作り方が画面と違う。無効の確認が API の入口でも効くことを確かめる(PR #12 の AIレビューの Important)
    [Fact]
    public async Task 無効にした利用者は_正しいパスワードでもトークンを受け取れない()
    {
        var api = Api();
        await CreateUserAsync("sato", Password, Roles.Sales);
        var expected = await DetailOf(await LoginAsync(api, "sato", "wrong password!!"));
        using (var identity = CreateIdentitySession())
        {
            (await identity.Users.SingleAsync(u => u.UserName == "sato")).IsActive = false;
            await identity.SaveChangesAsync();
        }

        var response = await LoginAsync(api, "sato", Password);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(expected, await DetailOf(response));
        Assert.Null(api.DefaultRequestHeaders.Authorization);
    }

    // 入口の LoginRequest は null を許す。500 にせず、パスワード違いと同じ 401 にする(PR #12 の AIレビューの Minor)
    [Fact]
    public async Task 利用者名かパスワードが無くても_500でなく同じ401()
    {
        var api = Api();
        await CreateUserAsync("sato", Password, Roles.Sales);
        var expected = await DetailOf(await LoginAsync(api, "sato", "wrong password!!")); // 応答の中身は1回しか読めない

        foreach (var body in new object[] { new { }, new { userName = "sato" }, new { userName = "", password = "" } })
        {
            var response = await api.PostAsJsonAsync("/account/login", body);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(expected, await DetailOf(response));
        }
    }

    [Fact]
    public async Task 続けて5回間違えると_正しいパスワードでもログインできない()
    {
        var api = Api();
        await CreateUserAsync("sato", Password, Roles.Sales);
        for (var i = 0; i < 5; i++) await LoginAsync(api, "sato", "wrong password!!");

        var response = await LoginAsync(api, "sato", Password);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("ロック", await DetailOf(response));
    }

    [Fact]
    public async Task トークンが無ければ_401で_Bearerのトークンを求める()
    {
        await new TestScenario(Db).SeedMastersAsync();

        var response = await Api().PostAsJsonAsync("/sales-orders", NewOrder());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
    }

    [Fact]
    public async Task トークンを付ければ_その利用者として操作でき_作成者に入る()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var api = Api();
        await CreateUserAsync("sato", Password, Roles.Sales);
        await LoginAsync(api, "sato", Password);

        var order = await CreateOrder(api, new CreateSalesOrderLine("P001", 1m));

        var createdBy = await Db.Database
            .SqlQueryRaw<string?>("SELECT created_by AS \"Value\" FROM sales_orders WHERE id = {0}", order.Id).SingleAsync();
        var satoId = await Db.Database
            .SqlQueryRaw<string>("SELECT \"Id\" AS \"Value\" FROM \"AspNetUsers\" WHERE \"UserName\" = 'sato'").SingleAsync();
        Assert.Equal(satoId, createdBy);
    }

    // 権限の実効性: 同じ締めの要求が、営業なら拒まれ、経理なら通る。営業の時は請求書が作られない。
    // 対照実験(2026-10-28): InvoiceUseCases.CloseAsync の権限の検査を外すと、営業でも 201 になってこのテストが落ちる
    [Fact]
    public async Task 営業が締めのAPIを呼ぶと_403で請求書は作られず_経理なら締められる()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var sales = Api();
        await CreateUserAsync("sato", Password, Roles.Sales);
        await CreateUserAsync("suzuki", Password, Roles.Accounting);
        await LoginAsync(sales, "sato", Password);
        var order = await CreateOrder(sales, new CreateSalesOrderLine("P001", 1m));
        await Send<SalesOrderView>(sales, $"/sales-orders/{order.Id}/approve");
        await Ship(sales, order.Id, new DateOnly(2026, 10, 5), (order.Lines[0].Id, 1m));
        var close = new CloseInvoice("C001", new DateOnly(2026, 10, 31));

        var problem = await AssertProblem(sales, "/invoices", HttpStatusCode.Forbidden, close);
        Assert.Contains("経理・管理者", problem.Detail);
        Assert.Equal(0, await Db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM invoices").SingleAsync());

        var accounting = Api();
        await LoginAsync(accounting, "suzuki", Password);
        await Send<InvoiceView>(accounting, "/invoices", close, HttpStatusCode.Created);
    }

    [Fact]
    public async Task トークンは_1時間たつと使えない()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var api = Api();
        await CreateUserAsync("sato", Password, Roles.Sales);
        await LoginAsync(api, "sato", Password);
        var order = await CreateOrder(api, new CreateSalesOrderLine("P001", 1m));

        Clock.Now += TimeSpan.FromMinutes(59);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync($"/sales-orders/{order.Id}")).StatusCode);

        Clock.Now += TimeSpan.FromMinutes(1);
        var expired = await api.GetAsync($"/sales-orders/{order.Id}");
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        // 期限切れも、トークンが無い時と同じく Bearer を求める(業務ルール集「8.」。PR #12 の AIレビュー3回目の Minor)
        Assert.Equal("Bearer", expired.Headers.WwwAuthenticate.Single().Scheme);
    }

    // 期限が切れたらログインし直す(本人の決定)。Identity の標準の更新の口(/refresh)は作らない。
    // ログインの応答には更新用のトークンが入っているので、口を足すと、配り済みのトークンが期限を延ばせる資格情報になる
    // (PR #12 の AIレビューの Minor)。このテストが落ちたら、口を足す前に本人に確かめる
    [Fact]
    public async Task トークンを更新する口は無い()
    {
        var response = await Api().PostAsJsonAsync("/account/refresh", new { refreshToken = "x" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static CreateSalesOrder NewOrder() => new("C002", new DateOnly(2026, 10, 1), [new CreateSalesOrderLine("P001", 1m)]);

    private static async Task<string?> DetailOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Detail;
}
