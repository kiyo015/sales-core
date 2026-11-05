using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SalesCore.Application;
using SalesCore.Domain;
using SalesCore.Web.Components.Pages.Invoices;

namespace SalesCore.Integration.Tests;

/// <summary>
/// 締め請求の画面(bUnit)。ユースケースは本物で、DB はテストのトランザクションを使う。
/// 完了条件(Day44): 経理が画面で締め請求でき、営業は締めの画面を開けない。
/// 規則の定義元は業務ルール集「4. 請求」と「役割」(締め請求は経理と管理者)。
/// </summary>
public class InvoicePageTests(DatabaseFixture fixture) : DatabaseTest(fixture), IDisposable
{
    private readonly BunitContext _screen = new();

    // ユースケースは本物の DB を使うので、描き直しを待つ時間を長めにする(MasterPageTests と同じ)
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    // 日本時間 2026-11-05 10:00。実行する日と重ならない日にする(Day43 の学び)
    private static readonly FixedClock Clock = new(new DateTimeOffset(2026, 11, 5, 1, 0, 0, TimeSpan.Zero));

    private static readonly DateOnly October31 = new(2026, 10, 31);

    // ── 締め ──
    // 支店(C002)の売上は、請求先の本社(C001)で締める。締め日の初期値は、選んだ請求先の今日以前で最近の締め日
    [Fact]
    public async Task 経理は請求先を選ぶと締め日の初期値が入り_締めると請求書の画面に移る()
    {
        await SeedShippedAsync();
        var page = OpenNew(Roles.Accounting);
        WaitForForm(page);

        Type(page, "#billing-customer", "C001");
        page.WaitForAssertion(() => Assert.Equal("2026-10-31", page.Find("#closing-date").GetAttribute("value")), Wait);
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Matches(@"/invoices/\d+$", Navigation.Uri), Wait);
        var id = long.Parse(Navigation.Uri.Split('/').Last());
        var invoice = await Invoices(Roles.Accounting).GetAsync(id);
        Assert.Equal(("C001", October31, 347m), (invoice.BillingCustomerCode, invoice.ClosingDate, invoice.TotalAmount));
    }

    // 請求書は請求先(請求先を持たない得意先)で締める。支店は選択肢に出さない
    [Fact]
    public async Task 請求先の選択肢は_請求先を持たない得意先だけ()
    {
        await SeedShippedAsync();
        var page = OpenNew(Roles.Accounting);
        WaitForForm(page);

        var options = page.FindAll("#billing-customer option").Select(o => o.GetAttribute("value")).Where(v => v != "").ToList();
        Assert.Equal(["C001", "C003"], options);
    }

    // 完了条件: 営業は締めの画面を開けない。URL を直接開いても、入口と同じ表で拒む
    [Fact]
    public void 営業が締めの画面を開くと_理由が出て_入力欄は出ない()
    {
        var page = OpenNew(Roles.Sales);

        page.WaitForAssertion(() => Assert.Equal("403", page.Find("[role=alert]").GetAttribute("data-status")), Wait);
        Assert.Empty(page.FindAll("form"));
    }

    [Fact]
    public async Task 請求する売上が無い請求先で締めると_理由が出る()
    {
        await SeedShippedAsync();
        var page = OpenNew(Roles.Accounting);
        WaitForForm(page);

        Type(page, "#billing-customer", "C003");
        page.WaitForAssertion(() => Assert.Equal("2026-10-31", page.Find("#closing-date").GetAttribute("value")), Wait);
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Contains("請求する売上が無い", page.Find("[role=alert]").TextContent), Wait);
        Assert.Equal("409", page.Find("[role=alert]").GetAttribute("data-status"));
        Assert.EndsWith("/", Navigation.Uri); // 移らない
    }

    [Fact]
    public async Task 締め日でない日で締めると_理由が出る()
    {
        await SeedShippedAsync();
        var page = OpenNew(Roles.Accounting);
        WaitForForm(page);

        Type(page, "#billing-customer", "C001");
        Type(page, "#closing-date", "2026-10-30");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Contains("締め日は月末", page.Find("[role=alert]").TextContent), Wait);
        Assert.Equal("400", page.Find("[role=alert]").GetAttribute("data-status"));
    }

    [Fact]
    public async Task 請求先を選ばずに締めると_選ぶよう求める()
    {
        await SeedShippedAsync();
        var page = OpenNew(Roles.Accounting);
        WaitForForm(page);

        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Contains("請求先を選ぶ", page.Find("[role=alert]").TextContent), Wait);
        Assert.Equal("400", page.Find("[role=alert]").GetAttribute("data-status"));
    }

    // ── 詳細 ──
    // 請求書には税率ごとの対象額と税額を出す(業務ルール集「2. 消費税」)。営業も請求書は見られる
    [Fact]
    public async Task 請求書の画面には_金額と税率ごとの内訳と売上の番号が出る()
    {
        await SeedShippedAsync();
        var invoice = await Invoices(Roles.Accounting).CloseAsync(new CloseInvoice("C001", October31));
        var page = OpenDetail(invoice.Id, Roles.Sales);
        page.WaitForAssertion(() => Assert.Contains(invoice.Number, page.Find("h1").TextContent), Wait);

        Assert.Equal("C001", page.Find("dd.billing-customer").TextContent.Trim());
        Assert.Equal("2026-10-01 〜 2026-10-31", page.Find("dd.period").TextContent.Trim());
        Assert.Equal("確定", page.Find("dd.status").TextContent.Trim());
        Assert.Equal(["10", "315", "32"], page.FindAll("table.tax-summaries tbody tr td").Select(td => td.TextContent.Trim()));
        Assert.Equal("347", page.Find("td.total-amount").TextContent.Trim());
        Assert.Equal(invoice.SalesRecordNumbers, page.FindAll("ul.sales-records li").Select(li => li.TextContent.Trim()));
    }

    [Fact]
    public void 無い請求書を開くと_理由が出る()
    {
        var page = OpenDetail(999_999_999, Roles.Accounting);

        page.WaitForAssertion(() => Assert.Equal("404", page.Find("[role=alert]").GetAttribute("data-status")), Wait);
    }

    // ── 一覧 ──
    [Fact]
    public async Task 請求書の一覧は新しい順で_状態は日本語()
    {
        await SeedShippedAsync(otherCompany: true);
        var accounting = Invoices(Roles.Accounting);
        var first = await accounting.CloseAsync(new CloseInvoice("C001", October31));
        var second = await accounting.CloseAsync(new CloseInvoice("C003", October31));
        var page = OpenList(Roles.Sales);
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("tbody tr").Count), Wait);

        Assert.Equal([second.Number, first.Number], page.FindAll("tbody tr td:first-child").Select(td => td.TextContent.Trim()));
        Assert.Equal(["確定", "確定"], page.FindAll("tbody tr td.status").Select(td => td.TextContent.Trim()));
        Assert.Equal(first.TotalAmount.ToString("#,0"), page.FindAll("tbody tr td.amount")[1].TextContent.Trim());
    }

    // 締めの入口は、締め請求のできる役割(経理・管理者)にだけ出す
    [Theory]
    [InlineData(Roles.Accounting, true)]
    [InlineData(Roles.Admin, true)]
    [InlineData(Roles.Sales, false)]
    public void 締めの入口は経理と管理者にだけ出る(string role, bool shown)
    {
        var page = OpenList(role);
        page.WaitForAssertion(() => Assert.NotNull(page.Find("table.invoices")), Wait); // 一覧を読み終えるまで待つ(読み込みの途中でテストを終えない)

        Assert.Equal(shown, page.FindAll("a.new-invoice").Count == 1);
    }

    // 状態を足して表示名を書き忘れると、英語の名前(Confirmed など)がそのまま画面に出る
    [Fact]
    public void すべての請求書の状態に表示名がある()
    {
        Assert.Equal(Enum.GetNames<InvoiceStatus>().Order(), InvoiceStatusText.Labels.Keys.Order());
    }

    public void Dispose() => _screen.Dispose();

    private NavigationManager Navigation => _screen.Services.GetRequiredService<NavigationManager>();

    private IRenderedComponent<NewInvoice> OpenNew(params string[] roles)
    {
        Register(roles);
        return _screen.Render<NewInvoice>();
    }

    private IRenderedComponent<InvoiceDetail> OpenDetail(long id, params string[] roles)
    {
        Register(roles);
        return _screen.Render<InvoiceDetail>(p => p.Add(d => d.Id, id));
    }

    private IRenderedComponent<InvoiceList> OpenList(params string[] roles)
    {
        Register(roles);
        return _screen.Render<InvoiceList>();
    }

    // ユースケースは、その利用者で、このテストのトランザクションを使う
    private void Register(string[] roles)
    {
        _screen.Services.AddSingleton<ICurrentUser>(TestUser.With(roles));
        _screen.Services.TryAddSingleton<TimeProvider>(Clock);
        _screen.Services.AddSingleton(Invoices(roles));
    }

    private InvoiceUseCases Invoices(params string[] roles) => new(Sessions(TestUser.With(roles)), Clock, TestUser.With(roles));

    // 支店(C002、請求先は本社 C001)に 105円(10%)を3箱、10/5 に出荷。本社の10月末の請求書: 315円、税 32円、計 347円。
    // otherCompany なら、別の会社(C003)にも 10/6 に1箱
    private async Task SeedShippedAsync(bool otherCompany = false)
    {
        var scenario = new TestScenario(Db);
        var m = await scenario.SeedMastersAsync();
        await ShipAsync(scenario, m, m.Branch, 3m, new DateOnly(2026, 10, 5));
        if (otherCompany) await ShipAsync(scenario, m, m.OtherCustomer, 1m, new DateOnly(2026, 10, 6));
        await scenario.EndRequestAsync();
    }

    private async Task ShipAsync(TestScenario scenario, TestScenario.MasterData m, Customer customer, decimal quantity, DateOnly shippedOn)
    {
        var order = scenario.AddOrder(customer, new DateOnly(2026, 10, 1), (m.StandardProduct, quantity, TaxRate.Of(10m)));
        order.Approve();
        var shipment = scenario.AddShipment(order.InstructShipment([new(order.Lines[0], quantity)]), m.Warehouse);
        await Db.SaveChangesAsync();
        scenario.AddSalesRecord(shipment.Confirm(shippedOn));
        await Db.SaveChangesAsync();
    }

    // 請求先の選択肢が読み込まれてから操作する
    private static void WaitForForm(IRenderedComponent<NewInvoice> page) =>
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("#billing-customer option[value=C001]")), Wait);

    // 入力のたびに描き直されるので、要素は毎回探し直し、値が画面に入ったのを見てから次へ進む(MasterPageTests と同じ)
    private static void Type<TPage>(IRenderedComponent<TPage> page, string selector, string value) where TPage : IComponent
    {
        page.Find(selector).Change(value);
        page.WaitForAssertion(() => Assert.Equal(value, page.Find(selector).GetAttribute("value") ?? ""), Wait);
    }
}
