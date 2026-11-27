using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using SalesCore.Application;
using SalesCore.Domain;
using SalesCore.Web.Components.Pages.MonthlyClosings;

namespace SalesCore.Integration.Tests;

/// <summary>
/// 月次締めの画面と売掛金の残高の一覧(bUnit)。ユースケースは本物で、DB はテストのトランザクションを使う。
/// 完了条件(Day59): 画面で月次締めができ、残高の一覧が出る。一覧は締めた月だけ、月を選んで請求先ごと、
/// 請求先を選んで月ごとの推移(2026-11-27 本人の決定)。
/// 今日は日本時間の 2030-12-01 0:30(UTC ではまだ 11/30)。11月が終わったかを UTC で決める誤りが見えるように。
/// 本社 C001: 10月 1,050 ＋ 税105 ＝ 1,155。11月 1,155 ＋ 2,100 ＋ 税210 − 1,000 ＝ 2,465。
/// 別の会社 C003: 11月に請求書の無い入金 300 だけ → −300(前受)。11月の合計は 2,165。
/// </summary>
public class MonthlyClosingPageTests(DatabaseFixture fixture) : DatabaseTest(fixture), IDisposable
{
    private readonly List<BunitContext> _screens = [];
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly TimeProvider _clock = new FixedClock(new DateTimeOffset(2030, 12, 1, 0, 30, 0, TimeSpan.FromHours(9)));

    // 次に締める月を出し、締めると詳細の画面に移る。締めた月は一覧に出て、次は翌月。終わっていない月はボタンを押せない
    [Fact]
    public async Task 経理が次に締める月を締めると_詳細に移り_一覧に出て_終わっていない月は締められない()
    {
        await AddTransactionsAsync();
        var page = Open<MonthlyClosingList>(Roles.Accounting);
        page.WaitForAssertion(() => Assert.Equal("2030年10月", page.Find(".next-month").TextContent), Wait); // 取引の一番古い月

        page.Find("button.close-month").Click();

        page.WaitForAssertion(() => Assert.EndsWith("/monthly-closings/2030/10", Navigation.Uri), Wait);
        var list = Open<MonthlyClosingList>(Roles.Accounting);
        list.WaitForAssertion(() => Assert.Equal("2030年11月", list.Find(".next-month").TextContent), Wait);
        Assert.Equal(["2030年10月 1,155"], list.FindAll("table.monthly-closings tbody tr").Select(Text));
        Assert.False(list.Find("button.close-month").HasAttribute("disabled")); // 日本時間では11月は終わっている

        list.Find("button.close-month").Click();

        list.WaitForAssertion(() => Assert.EndsWith("/monthly-closings/2030/11", Navigation.Uri), Wait); // 締め終わってから次の画面で DB を使う
        page = Open<MonthlyClosingList>(Roles.Accounting);
        page.WaitForAssertion(() => Assert.Equal("2030年12月", page.Find(".next-month").TextContent), Wait);
        Assert.True(page.Find("button.close-month").HasAttribute("disabled"));
        Assert.Contains("月末を過ぎてから", page.Find(".not-ended").TextContent);
        Assert.Equal(["2030年11月 2,165", "2030年10月 1,155"], page.FindAll("table.monthly-closings tbody tr").Select(Text)); // 新しい順
    }

    // 締められない理由(締め忘れなど)は、画面に出して移らない
    [Fact]
    public async Task 締められない時は理由を出して移らない()
    {
        var scenario = new TestScenario(Db);
        var m = await scenario.SeedMastersAsync();
        await scenario.AddInvoiceAsync(m, new DateOnly(2030, 10, 31), 10m);
        // 11月の売上を、11/30 締めの請求書を作らずに残す
        var order = scenario.AddOrder(m.HeadOffice, new DateOnly(2030, 11, 1), (m.StandardProduct, 1m, TaxRate.Of(10m)));
        order.Approve();
        var shipment = scenario.AddShipment(order.InstructShipment([new(order.Lines[0], 1m)]), m.Warehouse);
        await Db.SaveChangesAsync();
        scenario.AddSalesRecord(shipment.Confirm(new DateOnly(2030, 11, 10)));
        await scenario.EndRequestAsync();
        await Months(Roles.Accounting).CloseAsync(2030, 10);
        var page = Open<MonthlyClosingList>(Roles.Accounting);
        page.WaitForAssertion(() => Assert.Equal("2030年11月", page.Find(".next-month").TextContent), Wait);

        page.Find("button.close-month").Click();

        page.WaitForAssertion(() => Assert.Contains("C001(2030-11-30 締め)", page.Find(".operation-error").TextContent), Wait);
        Assert.EndsWith("/", Navigation.Uri);
    }

    // 取引がまだ無ければ、締める月は無いと出してボタンを出さない(PR #32 の AIレビュー)
    [Fact]
    public async Task 取引が無ければ_締める月は無いと出してボタンを出さない()
    {
        await new TestScenario(Db).SeedMastersAsync();

        var page = Open<MonthlyClosingList>(Roles.Accounting);

        page.WaitForAssertion(() => Assert.Contains("締める取引（売上・入金）がありません", page.Find("section.close").TextContent), Wait);
        Assert.Empty(page.FindAll("button.close-month"));
        Assert.Empty(page.FindAll("table.monthly-closings tbody tr"));
    }

    // 参照は全員。営業には締めの欄を出さない(次に締める月の問い合わせは経理と管理者の行なので、呼ばない)
    [Fact]
    public async Task 営業には締めの欄を出さず_締めた月の一覧だけを出す()
    {
        await AddTransactionsAsync();
        await Months(Roles.Accounting).CloseAsync(2030, 10);

        var page = Open<MonthlyClosingList>(Roles.Sales);

        page.WaitForAssertion(() => Assert.Equal(["2030年10月 1,155"], page.FindAll("table.monthly-closings tbody tr").Select(Text)), Wait);
        Assert.Empty(page.FindAll("button.close-month"));
        Assert.Empty(page.FindAll(".operation-error"));
    }

    // 月を選ぶと請求先ごとの残高と合計。前受は負の額。請求先を選ぶと、その請求先の月ごとの推移(古い順)
    [Fact]
    public async Task 月の詳細に請求先ごとの残高と合計を出し_請求先の推移に移れる()
    {
        await AddTransactionsAsync();
        await Months(Roles.Accounting).CloseAsync(2030, 10);
        await Months(Roles.Accounting).CloseAsync(2030, 11);

        var detail = Screen([Roles.Sales]).Render<MonthlyClosingDetail>(p => p.Add(d => d.Year, 2030).Add(d => d.Month, 11));

        detail.WaitForAssertion(() => Assert.Equal(
            ["C001 本社 1,155 2,100 210 1,000 2,465", "C003 別の会社 0 0 0 300 -300"],
            detail.FindAll("table.balances tbody tr").Select(Text)), Wait);
        Assert.Equal("合計 1,155 2,100 210 1,300 2,165", Text(detail.Find("table.balances tfoot tr")));
        Assert.Equal("receivable-balances/C001", detail.Find("table.balances tbody a").GetAttribute("href"));

        var history = Screen([Roles.Sales]).Render<ReceivableHistory>(p => p.Add(h => h.Code, "C001"));

        history.WaitForAssertion(() => Assert.Contains("C001 本社", history.Find("h1").TextContent), Wait);
        Assert.Equal(
            ["2030年10月 0 1,050 105 0 1,155", "2030年11月 1,155 2,100 210 1,000 2,465"],
            history.FindAll("table.history tbody tr").Select(Text));
    }

    // 締めていない月を URL で開いても、残高は出さずに理由を出す(2026-11-27 本人の決定)
    [Fact]
    public async Task 締めていない月を開くと_理由を出す()
    {
        await AddTransactionsAsync();

        var detail = Screen([Roles.Sales]).Render<MonthlyClosingDetail>(p => p.Add(d => d.Year, 2030).Add(d => d.Month, 11));

        detail.WaitForAssertion(() => Assert.Contains("2030年11月は月次締めしていない", detail.Find(".operation-error").TextContent), Wait);
        Assert.Empty(detail.FindAll("table.balances"));
    }

    public void Dispose() => _screens.ForEach(s => s.Dispose());

    private NavigationManager Navigation => _screens[^1].Services.GetRequiredService<NavigationManager>();

    private async Task AddTransactionsAsync()
    {
        var scenario = new TestScenario(Db);
        var m = await scenario.SeedMastersAsync();
        await scenario.AddInvoiceAsync(m, new DateOnly(2030, 10, 31), 10m);
        await scenario.AddInvoiceAsync(m, new DateOnly(2030, 11, 30), 20m);
        await scenario.EndRequestAsync();
        await Receipts(Roles.Accounting).RegisterAsync(new RegisterReceipt("C001", new DateOnly(2030, 11, 5), 1000m));
        await Receipts(Roles.Accounting).RegisterAsync(new RegisterReceipt("C003", new DateOnly(2030, 11, 10), 300m));
    }

    private IRenderedComponent<TPage> Open<TPage>(params string[] roles) where TPage : IComponent => Screen(roles).Render<TPage>();

    private BunitContext Screen(string[] roles)
    {
        var screen = new BunitContext();
        _screens.Add(screen);
        screen.Services.AddSingleton<ICurrentUser>(TestUser.With(roles));
        screen.Services.AddSingleton(_clock);
        screen.Services.AddSingleton(Months(roles));
        return screen;
    }

    private MonthlyClosingUseCases Months(params string[] roles) => new(Sessions(TestUser.With(roles)), _clock, TestUser.With(roles));

    private ReceiptUseCases Receipts(params string[] roles) => new(Sessions(TestUser.With(roles)), TestUser.With(roles), _clock);

    private static string Text(AngleSharp.Dom.IElement row) =>
        string.Join(' ', row.QuerySelectorAll("td, th").Select(td => td.TextContent.Trim()).Where(t => t != ""));
}
