using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SalesCore.Application;
using SalesCore.Domain;
using SalesCore.Web.Components.Pages.Orders;

namespace SalesCore.Integration.Tests;

/// <summary>
/// 受注の画面(bUnit)。ユースケースは本物で、DB はテストのトランザクションを使う。
/// 完了条件(Day42): 画面から受注を登録・承認・取消でき、権限の無い操作のボタンは出ず、URL を直接開いても拒否される。
/// </summary>
public class SalesOrderPageTests(DatabaseFixture fixture) : DatabaseTest(fixture), IDisposable
{
    private readonly BunitContext _screen = new();

    // ユースケースは本物の DB を使うので、描き直しを待つ時間を長めにする(MasterPageTests と同じ)
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static readonly DateOnly Day = new(2026, 11, 2);

    [Fact]
    public async Task 営業は明細を入れて受注を登録でき_詳細の画面に移る()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var page = Open<NewSalesOrder>(Roles.Sales);
        WaitForOptions(page);

        Type(page, "#customer", "C002");
        Type(page, "#ordered-on", "2026-11-02");
        Type(page, "#product-0", "P001");
        Type(page, "#quantity-0", "3");
        page.Find("button.add-line").Click();
        Type(page, "#product-1", "P002");
        Type(page, "#quantity-1", "2");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Matches(@"/orders/\d+$", Navigation.Uri), Wait);
        var order = await Orders(Roles.Sales).GetAsync(long.Parse(Navigation.Uri.Split('/')[^1]));
        Assert.Equal(("Received", "C002", Day), (order.Status, order.CustomerCode, order.OrderedOn));
        Assert.Equal([("P001", 3m, 105m), ("P002", 2m, 33.33m)], order.Lines.Select(l => (l.ProductCode, l.Quantity, l.UnitPrice)));
    }

    // 単価は、明細で指定しなければ商品の標準単価(業務ルール集「受注の単価と税率」)。画面では選んだ時に入れておき、書き換えられる
    [Fact]
    public async Task 商品を選ぶと標準単価が入り_書き換えた単価で受注する()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var page = Open<NewSalesOrder>(Roles.Sales);
        WaitForOptions(page);

        Type(page, "#customer", "C001");
        Type(page, "#product-0", "P002");
        page.WaitForAssertion(() => Assert.Equal("33.33", page.Find("#unit-price-0").GetAttribute("value")), Wait);
        Type(page, "#quantity-0", "1");
        Type(page, "#unit-price-0", "30");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Matches(@"/orders/\d+$", Navigation.Uri), Wait);
        var order = await Orders(Roles.Sales).GetAsync(long.Parse(Navigation.Uri.Split('/')[^1]));
        Assert.Equal(30m, order.Lines.Single().UnitPrice);
    }

    [Fact]
    public async Task 得意先を選ばずに登録すると_理由が出て_登録しない()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var page = Open<NewSalesOrder>(Roles.Sales);
        WaitForOptions(page);

        Type(page, "#product-0", "P001");
        Type(page, "#quantity-0", "1");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Contains("得意先を選ぶ", page.Find("[role=alert]").TextContent), Wait);
        Assert.Equal("400", page.Find("[role=alert]").GetAttribute("data-status"));
        Assert.EndsWith("/", Navigation.Uri);
    }

    // 無効にしたマスタでは受注できない(ユースケースも拒む)。選択肢に出さない
    [Fact]
    public async Task 無効にした得意先と商品は_選択肢に出ない()
    {
        var m = await new TestScenario(Db).SeedMastersAsync();
        await Db.Database.ExecuteSqlAsync($"UPDATE customers SET is_active = false WHERE id = {m.OtherCustomer.Id}");
        await Db.Database.ExecuteSqlAsync($"UPDATE products SET is_active = false WHERE id = {m.ReducedProduct.Id}");
        var page = Open<NewSalesOrder>(Roles.Sales);
        WaitForOptions(page);

        Assert.Equal(["", "C001", "C002"], page.FindAll("#customer option").Select(o => o.GetAttribute("value")));
        Assert.Equal(["", "P001"], page.FindAll("#product-0 option").Select(o => o.GetAttribute("value")));
    }

    // URL を直接開いても、入口と同じ表で拒む(ボタンを隠すのは使い勝手のため)
    [Fact]
    public async Task 経理が登録の画面を開くと_権限が無い理由が出て_入力欄は出ない()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var page = Open<NewSalesOrder>(Roles.Accounting);

        page.WaitForAssertion(() => Assert.Equal("403", page.Find("[role=alert]").GetAttribute("data-status")), Wait);
        Assert.Empty(page.FindAll("form"));
    }

    [Theory]
    [InlineData(Roles.Sales, true)]
    [InlineData(Roles.Admin, true)]
    [InlineData(Roles.Accounting, false)]
    public async Task 一覧は新しい順で状態が日本語で出て_登録への入口は営業と管理者だけ(string role, bool canRegister)
    {
        var (older, newer) = await SeedTwoOrdersAsync();
        var page = Open<SalesOrders>(role);

        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("tbody tr").Count), Wait);
        var rows = page.FindAll("tbody tr");
        Assert.Contains($"orders/{newer.Id}", rows[0].InnerHtml);
        Assert.Contains($"orders/{older.Id}", rows[1].InnerHtml);
        Assert.Contains("受付", rows[0].TextContent);
        Assert.Contains("承認済", rows[1].TextContent);
        Assert.Equal(canRegister, page.FindAll("a.new-order").Count == 1);
    }

    [Fact]
    public async Task 営業は受付の受注を承認でき_承認のボタンが消える()
    {
        var order = await SeedOrderAsync(1m);
        var page = OpenDetail(order.Id, Roles.Sales);
        page.WaitForAssertion(() => Assert.Contains("受付", page.Find(".status").TextContent), Wait);

        page.Find("button.approve").Click();

        page.WaitForAssertion(() => Assert.Contains("承認済", page.Find(".status").TextContent), Wait);
        Assert.Contains("承認しました", page.Find("[role=status]").TextContent);
        Assert.Empty(page.FindAll("button.approve"));
        Assert.Equal("Approved", (await Orders(Roles.Sales).GetAsync(order.Id)).Status);
    }

    // 承認待ち(税抜合計100万円以上)を承認できるのは管理者だけ。取り消しは営業もできる(業務ルール集「承認」)
    [Theory]
    [InlineData(Roles.Sales, false)]
    [InlineData(Roles.Admin, true)]
    public async Task 承認待ちの受注の承認のボタンは管理者にだけ出る(string role, bool canApprove)
    {
        var order = await SeedOrderAsync(10_000m); // 105円 × 10,000 = 1,050,000円
        var page = OpenDetail(order.Id, role);
        page.WaitForAssertion(() => Assert.Contains("承認待ち", page.Find(".status").TextContent), Wait);

        Assert.Equal(canApprove, page.FindAll("button.approve").Count == 1);
        Assert.Single(page.FindAll("button.cancel"));
    }

    [Fact]
    public async Task 経理には承認と取消のボタンが出ず_明細は見える()
    {
        var order = await SeedOrderAsync(1m);
        var page = OpenDetail(order.Id, Roles.Accounting);
        page.WaitForAssertion(() => Assert.Contains("P001", page.Find("table").TextContent), Wait);

        Assert.Empty(page.FindAll("button.approve"));
        Assert.Empty(page.FindAll("button.cancel"));
    }

    [Fact]
    public async Task 営業は受注を取り消せ_取り消した受注にはボタンが出ない()
    {
        var order = await SeedOrderAsync(1m);
        var page = OpenDetail(order.Id, Roles.Sales);
        page.WaitForAssertion(() => Assert.Single(page.FindAll("button.cancel")), Wait);

        page.Find("button.cancel").Click();

        page.WaitForAssertion(() => Assert.Contains("取消", page.Find(".status").TextContent), Wait);
        Assert.Empty(page.FindAll("button.approve"));
        Assert.Empty(page.FindAll("button.cancel"));
    }

    // ボタンを出した後に、ほかの人が状態を変えていたら、押しても理由付きで止まる(409)
    [Fact]
    public async Task 開いている間にほかの人が取り消した受注を承認すると_理由が出る()
    {
        var order = await SeedOrderAsync(1m);
        var page = OpenDetail(order.Id, Roles.Sales);
        page.WaitForAssertion(() => Assert.Single(page.FindAll("button.approve")), Wait);
        await Orders(Roles.Sales).CancelAsync(order.Id);

        page.Find("button.approve").Click();

        page.WaitForAssertion(() => Assert.Equal("409", page.Find("[role=alert]").GetAttribute("data-status")), Wait);
        Assert.Contains("取消", page.Find(".status").TextContent); // 読み直した状態を出す
    }

    [Fact]
    public async Task 無い受注を開くと_見つからない理由が出る()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var page = OpenDetail(999_999_999, Roles.Sales);

        page.WaitForAssertion(() => Assert.Equal("404", page.Find("[role=alert]").GetAttribute("data-status")), Wait);
    }

    // 状態を足して表示名を書き忘れると、英語の名前(Received など)がそのまま画面に出る
    [Fact]
    public void すべての状態に表示名がある()
    {
        Assert.Equal(Enum.GetNames<SalesOrderStatus>().Order(), OrderStatusText.Labels.Keys.Order());
    }

    public void Dispose() => _screen.Dispose();

    private NavigationManager Navigation => _screen.Services.GetRequiredService<NavigationManager>();

    // 画面を開く。ユースケースは、その利用者で、このテストのトランザクションを使う
    private IRenderedComponent<TPage> Open<TPage>(params string[] roles) where TPage : IComponent
    {
        Register(roles);
        return _screen.Render<TPage>();
    }

    private IRenderedComponent<SalesOrderDetail> OpenDetail(long id, params string[] roles)
    {
        Register(roles);
        return _screen.Render<SalesOrderDetail>(p => p.Add(d => d.Id, id));
    }

    private void Register(string[] roles)
    {
        var user = TestUser.With(roles);
        _screen.Services.AddSingleton<ICurrentUser>(user);
        _screen.Services.AddSingleton(TimeProvider.System);
        _screen.Services.AddSingleton(Orders(roles));
        _screen.Services.AddSingleton(new MasterUseCases(Sessions(user), user));
    }

    private SalesOrderUseCases Orders(params string[] roles) =>
        new(Sessions(TestUser.With(roles)), TestUser.With(roles), ApprovalThreshold.Default);

    // 得意先 C001 に、標準税率の商品 P001(105円)を quantity 個
    private async Task<SalesOrder> SeedOrderAsync(decimal quantity)
    {
        var scenario = new TestScenario(Db);
        var masters = await scenario.SeedMastersAsync();
        var order = scenario.AddOrder(masters.HeadOffice, Day, (masters.StandardProduct, quantity, TaxRate.Of(10m)));
        await Db.SaveChangesAsync();
        return order;
    }

    private async Task<(SalesOrder Older, SalesOrder Newer)> SeedTwoOrdersAsync()
    {
        var scenario = new TestScenario(Db);
        var masters = await scenario.SeedMastersAsync();
        var older = scenario.AddOrder(masters.HeadOffice, Day, (masters.StandardProduct, 1m, TaxRate.Of(10m)));
        older.Approve();
        var newer = scenario.AddOrder(masters.Branch, Day, (masters.ReducedProduct, 1m, TaxRate.Of(8m)));
        await Db.SaveChangesAsync();
        return (older, newer);
    }

    // 選択肢が読み込まれてから操作する(最初の読み込みが終わってから)
    private static void WaitForOptions(IRenderedComponent<NewSalesOrder> page) =>
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("#product-0 option[value=P001]")), Wait);

    // 入力欄と選択肢の両方に使う。入力のたびに描き直されるので、要素は毎回探し直し、値が画面に入ったのを見てから次へ進む(MasterPageTests と同じ)
    private static void Type<TPage>(IRenderedComponent<TPage> page, string selector, string value) where TPage : IComponent
    {
        page.Find(selector).Change(value);
        page.WaitForAssertion(() => Assert.Equal(value, page.Find(selector).GetAttribute("value") ?? ""), Wait);
    }
}
