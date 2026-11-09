using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;
using static SalesCore.E2E.Tests.E2EScreens;

namespace SalesCore.E2E.Tests;

/// <summary>
/// 端から端までのテスト(Day45 の完了条件)。本物のブラウザで、発行した画面を操作する。
/// 営業がログイン → 受注 → 承認 → 分納で3回出荷、経理がログイン → 10月末で締める。
/// 請求額は、段階1の API だけで通したテスト(InvoiceApiTests の最初のテスト)と同じ 455円になる
/// (10%: 315円・税32円、8%: 100円・税8円。売上ごとに丸めると税41円で1円ずれる)。
/// </summary>
[Collection(WebCollection.Name)]
public sealed class SalesFlowE2ETests(WebAppFixture app, ITestOutputHelper output)
{
    [Fact]
    public async Task 営業が受注して分納で出荷し_経理が締めると_APIのテストと同じ請求額になる()
    {
        var data = await E2EData.SeedAsync(app);
        await using var salesContext = await app.Browser.NewContextAsync(new() { BaseURL = app.BaseUrl });
        await using var accountingContext = await app.Browser.NewContextAsync(new() { BaseURL = app.BaseUrl });
        var sales = await salesContext.NewPageAsync();
        var accounting = await accountingContext.NewPageAsync();
        var failures = new RequestFailures();
        failures.Watch(sales);
        failures.Watch(accounting);
        try
        {
            // ── 営業: 受注 → 承認 → 分納で3回出荷 ──
            await LogInAsync(sales, data.Sales);
            await RegisterOrderAsync(sales, data.Branch, "2026-10-01", (data.Bolt, "3"), (data.Tea, "3"));
            await Expect(sales.Locator("dd.status")).ToHaveTextAsync("受付");

            await sales.ClickAsync("button.approve");
            await Expect(sales.Locator("dd.status")).ToHaveTextAsync("承認済");

            await ShipAsync(sales, data, "2026-10-05", bolts: "1", tea: "2"); // 105 + Round(66.66)=67
            await Expect(sales.Locator("dd.status")).ToHaveTextAsync("一部出荷");
            await ShipAsync(sales, data, "2026-10-12", bolts: "1", tea: "");  // 105
            await ShipAsync(sales, data, "2026-10-19", bolts: "1", tea: "1"); // 105 + (Round(99.99) − 67)=33
            await Expect(sales.Locator("dd.status")).ToHaveTextAsync("出荷済");

            // ── 経理: 請求先(本社)を10月末で締める ──
            await LogInAsync(accounting, data.Accounting);
            await accounting.GotoAsync("/invoices/new");
            await accounting.Locator($"#billing-customer option[value='{data.HeadOffice}']").WaitForAsync(new() { State = WaitForSelectorState.Attached });
            await accounting.SelectOptionAsync("#billing-customer", data.HeadOffice);
            // 初期値(今日以前で最近の締め日)は実行する日で変わるので、締め日は入れる
            await accounting.FillAsync("#closing-date", "2026-10-31");
            await accounting.GetByRole(AriaRole.Button, new() { Name = "締める" }).ClickAsync();
            await accounting.WaitForURLAsync(new Regex(@"/invoices/\d+$"));

            await Expect(accounting.Locator("dd.billing-customer")).ToHaveTextAsync(data.HeadOffice);
            await Expect(accounting.Locator("table.tax-summaries tbody tr")).ToHaveTextAsync([new Regex(@"^10\s*315\s*32$"), new Regex(@"^8\s*100\s*8$")]);
            await Expect(accounting.Locator("td.total-amount")).ToHaveTextAsync("455");

            failures.AssertNone();
        }
        catch
        {
            await SaveScreenshotsAsync("sales-flow", sales, accounting);
            throw;
        }
        finally
        {
            output.WriteLine(app.WebLog);
        }
    }

    // 受注の画面から出荷を指示し、受注の画面に戻ってその出荷を確定する。空の数量の明細は今回は出さない
    private static async Task ShipAsync(IPage page, E2EData data, string shippedOn, string bolts, string tea)
    {
        await page.ClickAsync("a.new-shipment");
        await page.Locator($"#warehouse option[value='{data.Warehouse}']").WaitForAsync(new() { State = WaitForSelectorState.Attached });
        await page.SelectOptionAsync("#warehouse", data.Warehouse);
        await page.Locator("tbody tr", new() { HasText = data.Bolt }).Locator("input").FillAsync(bolts);
        await page.Locator("tbody tr", new() { HasText = data.Tea }).Locator("input").FillAsync(tea);
        await page.GetByRole(AriaRole.Button, new() { Name = "指示" }).ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/orders/\d+$"));

        // 指示のままの出荷は1件だけ。確定すると「指示」の行が無くなる
        var instructed = page.Locator("table.shipments tbody tr", new() { HasText = "指示を取消" });
        await Expect(instructed).ToHaveCountAsync(1);
        await instructed.Locator("input[type=date]").FillAsync(shippedOn);
        await instructed.Locator("button.confirm-shipment").ClickAsync();
        await Expect(instructed).ToHaveCountAsync(0);
    }
}
