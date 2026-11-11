using Microsoft.Playwright;
using SalesCore.Application;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;
using static SalesCore.E2E.Tests.E2EScreens;

namespace SalesCore.E2E.Tests;

/// <summary>
/// 画面での権限の実効性(Day46 の完了条件)。3つの役割それぞれでログインし、許された操作だけが画面に出て、
/// 許されない画面は URL を直接入れても開けないことを、本物のブラウザで確かめる。
/// <para>
/// 期待は業務ルール集「役割」の表から、ここに書き写す(画面と同じ <see cref="Permissions"/> から作ると、表の誤りを画面のテストが見逃す)。
/// 画面の守りは3段: ボタンやリンクを出さない(<see cref="Permissions.Allows"/>)、開いた時に画面が拒む、ユースケースの入口が拒む。
/// 画面の中では前の2段が先に止めるので、ユースケースの入口の検査は API と結合テストで守る
/// (対照実験の結果は、段階2の設計 docs/specs/2026-10-22-stage2-screens-and-permissions-design.md の「段階2の完了の確かめ方」)。
/// </para>
/// </summary>
[Collection(WebCollection.Name)]
public sealed class PermissionE2ETests(WebAppFixture app, ITestOutputHelper output)
{
    /// <summary>役割ごとにできること。受注の扱い = 承認・取消・出荷の指示。</summary>
    private sealed record Expected(bool RegisterOrder, bool HandleOrder, bool CloseInvoice, bool RegisterMaster);

    private static readonly Dictionary<string, Expected> ByRole = new()
    {
        [Roles.Sales] = new(RegisterOrder: true, HandleOrder: true, CloseInvoice: false, RegisterMaster: false),
        [Roles.Accounting] = new(RegisterOrder: false, HandleOrder: false, CloseInvoice: true, RegisterMaster: false),
        [Roles.Admin] = new(RegisterOrder: true, HandleOrder: true, CloseInvoice: true, RegisterMaster: true),
    };

    [Theory]
    [InlineData(Roles.Sales)]
    [InlineData(Roles.Accounting)]
    [InlineData(Roles.Admin)]
    public async Task 役割ごとに_できる操作だけが画面に出て_できない画面はURLを直接入れても開けない(string role)
    {
        var expected = ByRole[role];
        var data = await E2EData.SeedAsync(app);
        await using var salesContext = await app.Browser.NewContextAsync(new() { BaseURL = app.BaseUrl });
        await using var userContext = await app.Browser.NewContextAsync(new() { BaseURL = app.BaseUrl });
        var sales = await salesContext.NewPageAsync();
        var page = await userContext.NewPageAsync();
        var failures = new RequestFailures();
        failures.Watch(sales);
        failures.Watch(page);
        try
        {
            // 確かめる受注は営業が作る(受付)
            await LogInAsync(sales, data.Sales);
            var orderId = await RegisterOrderAsync(sales, data.Branch, "2026-10-01", (data.Bolt, "1"));
            await LogInAsync(page, data.UserOf(role));

            // メニュー: 参照は全員ができるので、全員に同じ画面が出る
            await Expect(page.Locator("nav.menu a")).ToHaveTextAsync(["トップ", "受注", "請求書", "得意先", "商品", "倉庫"]);

            // 一覧のリンクと、リンクの先を URL で直接開いた時。出ないことを確かめる前に、読み込みが終わるのを待つ(終わる前は何も出ていない)
            await page.GotoAsync("/orders");
            await Expect(page.Locator("tbody tr", new() { HasText = data.Branch })).ToHaveCountAsync(1);
            await ExpectShownAsync(page.Locator("a.new-order"), expected.RegisterOrder);
            await ExpectOpensAsync(page, "/orders/new", page.Locator("#customer"), expected.RegisterOrder);

            await page.GotoAsync("/invoices");
            await Expect(page.Locator("table.invoices")).ToBeVisibleAsync();
            await ExpectShownAsync(page.Locator("a.new-invoice"), expected.CloseInvoice);
            await ExpectOpensAsync(page, "/invoices/new", page.Locator("#billing-customer"), expected.CloseInvoice);

            // マスタ: 一覧は全員、登録の欄は管理者だけ
            foreach (var (path, code, legend) in new[]
            {
                ("/masters/customers", data.Branch, "得意先の登録"), // 本社の行は支店の行にも請求先として出るので、支店で待つ
                ("/masters/products", data.Bolt, "商品の登録"),
                ("/masters/warehouses", data.Warehouse, "倉庫の登録"),
            })
            {
                await page.GotoAsync(path);
                await Expect(page.Locator("tbody tr", new() { HasText = code })).ToHaveCountAsync(1);
                await ExpectShownAsync(page.Locator("legend", new() { HasText = legend }), expected.RegisterMaster);
            }

            // 受付の受注: 承認・取消
            await page.GotoAsync($"/orders/{orderId}");
            await Expect(page.Locator("dd.status")).ToHaveTextAsync("受付");
            await ExpectShownAsync(page.Locator("button.approve"), expected.HandleOrder);
            await ExpectShownAsync(page.Locator("button.cancel"), expected.HandleOrder);

            // 承認済の受注: 出荷の指示
            await sales.ClickAsync("button.approve");
            await Expect(sales.Locator("dd.status")).ToHaveTextAsync("承認済");
            await page.GotoAsync($"/orders/{orderId}");
            await Expect(page.Locator("dd.status")).ToHaveTextAsync("承認済");
            await ExpectShownAsync(page.Locator("a.new-shipment"), expected.HandleOrder);
            await ExpectOpensAsync(page, $"/orders/{orderId}/shipments/new", page.Locator("#warehouse"), expected.HandleOrder);

            failures.AssertNone();
        }
        catch
        {
            await SaveScreenshotsAsync($"permission-{role}", sales, page);
            throw;
        }
        finally
        {
            output.WriteLine(app.WebLog);
        }
    }

    [Fact]
    public async Task 承認待ちの受注は_営業には承認のボタンが出ず_管理者が承認できる()
    {
        var data = await E2EData.SeedAsync(app);
        await using var salesContext = await app.Browser.NewContextAsync(new() { BaseURL = app.BaseUrl });
        await using var adminContext = await app.Browser.NewContextAsync(new() { BaseURL = app.BaseUrl });
        var sales = await salesContext.NewPageAsync();
        var admin = await adminContext.NewPageAsync();
        var failures = new RequestFailures();
        failures.Watch(sales);
        failures.Watch(admin);
        try
        {
            // 税抜 105円 × 10,000箱 = 1,050,000円。基準額(100万円)以上なので承認待ち
            await LogInAsync(sales, data.Sales);
            var orderId = await RegisterOrderAsync(sales, data.Branch, "2026-10-01", (data.Bolt, "10000"));
            await Expect(sales.Locator("dd.status")).ToHaveTextAsync("承認待ち");
            await Expect(sales.Locator("button.cancel")).ToBeVisibleAsync();
            await Expect(sales.Locator("button.approve")).ToHaveCountAsync(0);

            await LogInAsync(admin, data.Admin);
            await admin.GotoAsync($"/orders/{orderId}");
            await admin.ClickAsync("button.approve");
            await Expect(admin.Locator("dd.status")).ToHaveTextAsync("承認済");

            failures.AssertNone();
        }
        catch
        {
            await SaveScreenshotsAsync("pending-approval", sales, admin);
            throw;
        }
        finally
        {
            output.WriteLine(app.WebLog);
        }
    }

    // 出す時は見えること、出さない時は1つも無いこと(隠すのでなく、描かない)
    private static Task ExpectShownAsync(ILocator locator, bool shown) =>
        shown ? Expect(locator).ToBeVisibleAsync() : Expect(locator).ToHaveCountAsync(0);

    // URL を直接開く。できる役割には入力の欄が出て、できない役割には権限が無いと出て、入力の欄は出ない
    private static async Task ExpectOpensAsync(IPage page, string url, ILocator input, bool allowed)
    {
        await page.GotoAsync(url);
        if (allowed)
        {
            await Expect(input).ToBeVisibleAsync();
            return;
        }
        await Expect(page.Locator(".operation-error[data-status='403']")).ToContainTextAsync("この操作をする権限がありません。");
        await Expect(input).ToHaveCountAsync(0);
    }
}
