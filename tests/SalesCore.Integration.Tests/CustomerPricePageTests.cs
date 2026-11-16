using Bunit;
using Microsoft.Extensions.DependencyInjection;
using SalesCore.Application;
using SalesCore.Web.Components.Pages.Masters;

namespace SalesCore.Integration.Tests;

/// <summary>
/// 得意先別単価の画面(bUnit)。一覧は全員、登録と無効化は管理者だけ(業務ルール集「役割」2026-11-13・11-16 本人の決定)。
/// ユースケースは本物で、DB はテストのトランザクションを使う。日付は 2030 年(書いた日・実行する日と重ならない)。
/// </summary>
public class CustomerPricePageTests(DatabaseFixture fixture) : DatabaseTest(fixture), IDisposable
{
    private readonly BunitContext _screen = new();

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task 管理者が改定すると_前の単価に末日が入り_両方が一覧に出る()
    {
        await new TestScenario(Db).SeedMastersAsync();
        await Masters(Roles.Admin).RegisterCustomerPriceAsync(new RegisterCustomerPrice("C001", "P001", 90m, new DateOnly(2030, 4, 1), null));
        var page = Open(Roles.Admin);
        page.WaitForAssertion(() => Assert.Single(page.FindAll("tbody tr")), Wait);

        Type(page, "#price-customer", "C001");
        Type(page, "#price-product", "P001");
        Type(page, "#unit-price", "80");
        Type(page, "#valid-from", "2030-10-01");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Contains("単価を登録しました", page.Find("[role=status]").TextContent), Wait);
        Assert.Contains("2030-04-01 からの単価の末日を 2030-09-30 にしました", page.Find("[role=status]").TextContent);
        Assert.Equal(
            ["C001 P001 90 2030-04-01 2030-09-30 有効", "C001 P001 80 2030-10-01 無期限 有効"],
            page.FindAll("tbody tr").Select(r => string.Join(" ", r.QuerySelectorAll("td").Take(6).Select(c => c.TextContent.Trim()))));
    }

    [Fact]
    public async Task 特価を登録すると_前の単価を翌日から続けたことが出る()
    {
        await new TestScenario(Db).SeedMastersAsync();
        await Masters(Roles.Admin).RegisterCustomerPriceAsync(new RegisterCustomerPrice("C001", "P001", 90m, new DateOnly(2030, 4, 1), null));
        var page = Open(Roles.Admin);
        page.WaitForAssertion(() => Assert.Single(page.FindAll("tbody tr")), Wait);

        Type(page, "#price-customer", "C001");
        Type(page, "#price-product", "P001");
        Type(page, "#unit-price", "80");
        Type(page, "#valid-from", "2030-10-01");
        Type(page, "#valid-to", "2030-10-31");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Contains("2030-11-01 から前の単価 90 を続けます", page.Find("[role=status]").TextContent), Wait);
        Assert.Equal(3, page.FindAll("tbody tr").Count);
    }

    // 支店には登録しない(受注は請求先の単価を使う)。選択肢に出さず、送られてもユースケースが拒む(CustomerPriceTests)
    [Fact]
    public async Task 得意先の選択肢は有効な請求先だけ()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var page = Open(Roles.Admin);

        page.WaitForAssertion(() => Assert.Equal(["", "C001", "C003"], page.FindAll("#price-customer option").Select(o => o.GetAttribute("value"))), Wait);
    }

    [Fact]
    public async Task 期間が重なると_理由が出る()
    {
        await new TestScenario(Db).SeedMastersAsync();
        await Masters(Roles.Admin).RegisterCustomerPriceAsync(new RegisterCustomerPrice("C001", "P001", 90m, new DateOnly(2030, 4, 1), null));
        var page = Open(Roles.Admin);
        page.WaitForAssertion(() => Assert.Single(page.FindAll("tbody tr")), Wait);

        Type(page, "#price-customer", "C001");
        Type(page, "#price-product", "P001");
        Type(page, "#unit-price", "80");
        Type(page, "#valid-from", "2030-04-01");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Contains("期間が重なる", page.Find("[role=alert]").TextContent), Wait);
        Assert.Equal("409", page.Find("[role=alert]").GetAttribute("data-status"));
    }

    [Fact]
    public async Task 単価が空のまま登録すると_送らずに理由を出す()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var page = Open(Roles.Admin);
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("#price-customer option[value=C001]")), Wait);

        Type(page, "#price-customer", "C001");
        Type(page, "#price-product", "P001");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Contains("単価を入力してください", page.Markup), Wait);
        Assert.Empty(await Masters(Roles.Admin).ListCustomerPricesAsync());
    }

    [Fact]
    public async Task 管理者が無効にすると_一覧で無効になり_ボタンが消える()
    {
        await new TestScenario(Db).SeedMastersAsync();
        await Masters(Roles.Admin).RegisterCustomerPriceAsync(new RegisterCustomerPrice("C001", "P001", 900m, new DateOnly(2030, 4, 1), null));
        var page = Open(Roles.Admin);
        page.WaitForAssertion(() => Assert.Single(page.FindAll("button.invalidate")), Wait);

        page.Find("button.invalidate").Click();
        Assert.True((await Masters(Roles.Admin).ListCustomerPricesAsync()).Single().IsActive); // 1回目は確かめるだけ
        page.Find("button.confirm-invalidate").Click();

        page.WaitForAssertion(() => Assert.Equal("無効", page.FindAll("tbody tr td")[5].TextContent), Wait);
        Assert.Empty(page.FindAll("button.invalidate, button.confirm-invalidate"));
        Assert.Contains("2030-04-01〜無期限 の受注は標準単価になります", page.Find("[role=status]").TextContent);
        Assert.False((await Masters(Roles.Admin).ListCustomerPricesAsync()).Single().IsActive);
    }

    // 隠すのは使い勝手のため。営業が登録・無効化のユースケースを呼んでも入口で拒むことは PermissionTests が確かめる
    [Theory]
    [InlineData(Roles.Sales)]
    [InlineData(Roles.Accounting)]
    public async Task 管理者でなければ_一覧は見えて_登録の欄と無効のボタンは出ない(string role)
    {
        await new TestScenario(Db).SeedMastersAsync();
        await Masters(Roles.Admin).RegisterCustomerPriceAsync(new RegisterCustomerPrice("C001", "P001", 90m, new DateOnly(2030, 4, 1), null));
        var page = Open(role);

        page.WaitForAssertion(() => Assert.Single(page.FindAll("tbody tr")), Wait);
        Assert.Empty(page.FindAll("form"));
        Assert.Empty(page.FindAll("button.invalidate"));
    }

    public void Dispose() => _screen.Dispose();

    private IRenderedComponent<CustomerPrices> Open(params string[] roles)
    {
        var user = TestUser.With(roles);
        _screen.Services.AddSingleton<ICurrentUser>(user);
        _screen.Services.AddSingleton(Masters(roles));
        return _screen.Render<CustomerPrices>();
    }

    private MasterUseCases Masters(params string[] roles) => new(Sessions(TestUser.With(roles)), TestUser.With(roles));

    // 入れた値が画面に入ったのを見てから次へ進む(MasterPageTests と同じ)
    private static void Type(IRenderedComponent<CustomerPrices> page, string selector, string value)
    {
        page.Find(selector).Change(value);
        page.WaitForAssertion(() => Assert.Equal(value, page.Find(selector).GetAttribute("value") ?? ""), Wait);
    }
}
