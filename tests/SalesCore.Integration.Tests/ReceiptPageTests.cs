using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SalesCore.Application;
using SalesCore.Web.Components.Pages.Invoices;
using SalesCore.Web.Components.Pages.Receipts;

namespace SalesCore.Integration.Tests;

/// <summary>
/// 入金の画面(bUnit)と、請求書の画面の未回収。ユースケースは本物で、DB はテストのトランザクションを使う。
/// 完了条件(Day56): 経理が入金を登録すると古い請求書から消し込まれ、権限の無い役割は画面でも拒まれる。
/// 今日は日本時間の 2030-12-10 0:30(UTC ではまだ 12/9)。請求書は本社 C001 の 10月締め 1,155円と 11月締め 2,310円で、
/// 11月を先に作り、Id の順と締め日の順を逆にしてある。
/// </summary>
public class ReceiptPageTests(DatabaseFixture fixture) : DatabaseTest(fixture), IDisposable
{
    // 1つのテストで役割を変えて何度も開くので、開くたびに作る(サービスは最初に描いた後は足せない)
    private readonly List<BunitContext> _screens = [];

    // ユースケースは本物の DB を使うので、描き直しを待つ時間を長めにする(SalesOrderPageTests と同じ)
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static readonly DateOnly ReceivedOn = new(2030, 12, 5);
    private readonly TimeProvider _clock = new FixedClock(new DateTimeOffset(2030, 12, 10, 0, 30, 0, TimeSpan.FromHours(9)));

    // 請求先を選ぶと、未回収の請求書を充てる順(締め日の古い順)に出す(2026-11-24 本人の決定)。登録すると詳細の画面に移る
    [Fact]
    public async Task 経理が請求先を選ぶと_未回収の請求書が充てる順に出て_登録すると詳細の画面に移る()
    {
        var (october, november) = await AddInvoicesAsync();
        var page = Open<NewReceipt>(Roles.Accounting);
        WaitForOptions(page);
        Assert.Equal("2030-12-10", page.Find("#received-on").GetAttribute("value")); // 日本時間の今日
        Assert.Equal(["C001", "C003"], page.FindAll("#billing-customer option").Select(o => o.GetAttribute("value")).Where(v => v != "")); // 支店は出さない

        Type(page, "#billing-customer", "C001");

        page.WaitForAssertion(() => Assert.Equal(
            [$"{october.Number} 2030-10-31 1,155", $"{november.Number} 2030-11-30 2,310"],
            page.FindAll("table.outstanding-invoices tbody tr").Select(Text)), Wait);
        Type(page, "#amount", "1500");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Matches(@"/receipts/\d+$", Navigation.Uri), Wait);
        var receipt = await Receipts(Roles.Accounting).GetAsync(long.Parse(Navigation.Uri.Split('/')[^1]));
        Assert.Equal(("C001", new DateOnly(2030, 12, 10), 1500m), (receipt.BillingCustomerCode, receipt.ReceivedOn, receipt.Amount));
        Assert.Equal([(october.Id, 1155m), (november.Id, 345m)], receipt.Allocations.Select(a => (a.InvoiceId, a.Amount)));
    }

    // 未回収は今回分のうち消し込んでいない額。消し終えた請求書は出さず、一部消した請求書は残りを出す
    [Fact]
    public async Task 消し終えた請求書は出さず_一部消した請求書は残りを出す()
    {
        var (_, november) = await AddInvoicesAsync();
        await Receipts(Roles.Accounting).RegisterAsync(new RegisterReceipt("C001", ReceivedOn, 1500m)); // 10月 1,155円・11月 345円
        var page = Open<NewReceipt>(Roles.Accounting);
        WaitForOptions(page);

        Type(page, "#billing-customer", "C001");

        page.WaitForAssertion(() => Assert.Equal([$"{november.Number} 2030-11-30 1,965"], page.FindAll("table.outstanding-invoices tbody tr").Select(Text)), Wait);
    }

    // 数として読めない値を入れた時は、前に入れていた額で登録しない(理由を出す)
    [Fact]
    public async Task 入金額に数でない値を入れると_前の値で登録せずに理由を出す()
    {
        await AddInvoicesAsync();
        var page = Open<NewReceipt>(Roles.Accounting);
        WaitForOptions(page);
        Type(page, "#billing-customer", "C001");
        Type(page, "#amount", "1500");
        Type(page, "#amount", "abc");
        var before = await Db.Receipts.CountAsync();

        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Contains("入金額は数で入力してください。", page.Find(".validation-errors").TextContent), Wait);
        Assert.Equal(before, await Db.Receipts.CountAsync());
    }

    // 取引をやめて無効にした請求先も選べる(2026-11-24 本人の決定)。未回収の請求書が無ければ、全額が前受になると知らせる
    [Fact]
    public async Task 無効にした請求先も選べ_未回収が無ければ前受になると出す()
    {
        await AddInvoicesAsync();
        await Db.Database.ExecuteSqlAsync($"UPDATE customers SET is_active = false WHERE code = 'C003'");
        var page = Open<NewReceipt>(Roles.Accounting);
        WaitForOptions(page);
        Assert.Contains("（無効）", page.Find("#billing-customer option[value=C003]").TextContent);

        Type(page, "#billing-customer", "C003");

        page.WaitForAssertion(() => Assert.Contains("すべて前受", page.Find(".no-outstanding").TextContent), Wait);
        Assert.Empty(page.FindAll("table.outstanding-invoices"));
    }

    // 入金額を空のまま送ると、0円にせずユースケースの理由を出す(値型の既定値で通さない)
    [Fact]
    public async Task 入金額を入れずに登録すると_登録せずに理由を出す()
    {
        await AddInvoicesAsync();
        var page = Open<NewReceipt>(Roles.Accounting);
        WaitForOptions(page);
        Type(page, "#billing-customer", "C001");
        var before = await Db.Receipts.CountAsync();

        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Contains("入金額を入れる。", page.Find("[role=alert]").TextContent), Wait);
        Assert.Equal(before, await Db.Receipts.CountAsync());
    }

    // 登録の画面を URL で直接開いても、入口と同じ表で拒み、入力欄を出さない
    [Fact]
    public async Task 営業が登録の画面を開くと_拒まれて入力欄が出ない()
    {
        await AddInvoicesAsync();
        var page = Open<NewReceipt>(Roles.Sales);

        page.WaitForAssertion(() => Assert.Contains("RegisterReceipt", page.Find("[role=alert]").TextContent), Wait);
        Assert.Empty(page.FindAll("#amount"));
    }

    // 取消は戻せないので、確かめてから取り消す(得意先別単価の無効化と同じ)。取り消すと打ち消しの入金の画面に移る
    [Fact]
    public async Task 経理は確かめてから取り消し_打ち消しの入金の画面に移る()
    {
        var (october, november) = await AddInvoicesAsync();
        var receipt = await Receipts(Roles.Accounting).RegisterAsync(new RegisterReceipt("C001", ReceivedOn, 1500m));
        var page = OpenDetail(receipt.Id, Roles.Accounting);
        page.WaitForAssertion(() => Assert.Equal("1,500", page.Find("dd.amount").TextContent), Wait);
        Assert.Equal([$"{october.Number} 2030-10-31 1,155", $"{november.Number} 2030-11-30 345"], page.FindAll("table.allocations tbody tr").Select(Text));
        Assert.Equal("0", page.Find("dd.unallocated").TextContent);

        page.Find("button.cancel").Click();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("button.confirm-cancel")), Wait);
        Assert.False((await Receipts(Roles.Accounting).GetAsync(receipt.Id)).IsCancelled); // 確かめる前は取り消さない
        page.Find("button.confirm-cancel").Click();

        page.WaitForAssertion(() => Assert.Matches(@"/receipts/\d+$", Navigation.Uri), Wait);
        var reversal = await Receipts(Roles.Accounting).GetAsync(long.Parse(Navigation.Uri.Split('/')[^1]));
        Assert.Equal((receipt.Id, -1500m, new DateOnly(2030, 12, 10)), (reversal.Reverses?.Id, reversal.Amount, reversal.ReceivedOn));
        Assert.True((await Receipts(Roles.Accounting).GetAsync(receipt.Id)).IsCancelled);
    }

    [Fact]
    public async Task 確かめる所でやめると_取り消さない()
    {
        await AddInvoicesAsync();
        var receipt = await Receipts(Roles.Accounting).RegisterAsync(new RegisterReceipt("C001", ReceivedOn, 1500m));
        var page = OpenDetail(receipt.Id, Roles.Accounting);
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("button.cancel")), Wait);

        page.Find("button.cancel").Click();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("button.keep")), Wait);
        page.Find("button.keep").Click();

        page.WaitForAssertion(() => Assert.Empty(page.FindAll("button.confirm-cancel")), Wait);
        Assert.False((await Receipts(Roles.Accounting).GetAsync(receipt.Id)).IsCancelled);
    }

    // 取消のボタンは、取り消せる入金(取り消していない・打ち消しでない)で、取り消せる役割(経理・管理者)にだけ出す
    [Fact]
    public async Task 取消のボタンは_取り消せる入金で_取り消せる役割にだけ出す()
    {
        await AddInvoicesAsync();
        var receipt = await Receipts(Roles.Accounting).RegisterAsync(new RegisterReceipt("C001", ReceivedOn, 1500m));

        var sales = OpenDetail(receipt.Id, Roles.Sales);
        sales.WaitForAssertion(() => Assert.Equal("1,500", sales.Find("dd.amount").TextContent), Wait);
        Assert.Empty(sales.FindAll("button.cancel"));

        var reversal = await Receipts(Roles.Accounting).CancelAsync(receipt.Id);
        foreach (var (id, status) in new[] { (receipt.Id, "取消済"), (reversal.Id, "打ち消し") })
        {
            var page = OpenDetail(id, Roles.Admin);
            page.WaitForAssertion(() => Assert.Equal(status, page.Find("dd.status").TextContent), Wait);
            Assert.Empty(page.FindAll("button.cancel"));
        }
    }

    // 一覧は全員が見られ、取り消した入金と打ち消しの入金を両方並べる(2026-11-24 本人の決定)。登録の入口は経理と管理者だけ
    [Fact]
    public async Task 一覧は取消済と打ち消しを並べ_登録の入口は経理と管理者にだけ出す()
    {
        await AddInvoicesAsync();
        var receipt = await Receipts(Roles.Accounting).RegisterAsync(new RegisterReceipt("C001", ReceivedOn, 5000m));
        var reversal = await Receipts(Roles.Accounting).CancelAsync(receipt.Id);

        var page = Open<ReceiptList>(Roles.Sales);

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("table.receipts tbody tr")), Wait);
        var rows = page.FindAll("table.receipts tbody tr").Select(Text).Where(t => t.StartsWith(receipt.Number) || t.StartsWith(reversal.Number));
        Assert.Equal(
            [$"{reversal.Number} C001 2030-12-10 -5,000 -1,535 {receipt.Number} の取消", $"{receipt.Number} C001 2030-12-05 5,000 1,535 取消済"],
            rows);
        Assert.Empty(page.FindAll("a.new-receipt"));
        Assert.NotEmpty(Open<ReceiptList>(Roles.Accounting).FindAll("a.new-receipt"));
    }

    // 請求書の一覧と詳細に、消し込んだ額と未回収を出す(請求額は変わらない)
    [Fact]
    public async Task 請求書の一覧と詳細に_未回収を出す()
    {
        var (october, november) = await AddInvoicesAsync();
        await Receipts(Roles.Accounting).RegisterAsync(new RegisterReceipt("C001", ReceivedOn, 1500m));

        var list = Open<InvoiceList>(Roles.Sales);
        list.WaitForAssertion(() => Assert.NotEmpty(list.FindAll("table.invoices tbody tr")), Wait);
        Assert.Equal(
            [$"{october.Number} C001 2030-10-31 確定 1,155 0", $"{november.Number} C001 2030-11-30 確定 2,310 1,965"], // 新しい順(10月を後に作った)
            list.FindAll("table.invoices tbody tr").Select(Text).Where(t => t.StartsWith(october.Number) || t.StartsWith(november.Number)));

        var detail = OpenInvoiceDetail(november.Id, Roles.Sales);
        detail.WaitForAssertion(() => Assert.Equal("1,965", detail.Find("td.outstanding").TextContent), Wait);
        Assert.Equal(("2,310", "345"), (detail.Find("td.total-amount").TextContent, detail.Find("td.allocated-amount").TextContent));
    }

    public void Dispose() => _screens.ForEach(s => s.Dispose());

    // 最後に開いた画面の移動先
    private NavigationManager Navigation => _screens[^1].Services.GetRequiredService<NavigationManager>();

    private async Task<(InvoiceView October, InvoiceView November)> AddInvoicesAsync()
    {
        var scenario = new TestScenario(Db);
        var m = await scenario.SeedMastersAsync();
        var november = await scenario.AddInvoiceAsync(m, new DateOnly(2030, 11, 30), 20m);
        var october = await scenario.AddInvoiceAsync(m, new DateOnly(2030, 10, 31), 10m);
        await scenario.EndRequestAsync();
        var invoices = Invoices(Roles.Admin);
        return (await invoices.GetAsync(october.Id), await invoices.GetAsync(november.Id));
    }

    // 画面を開く。ユースケースは、その利用者で、このテストのトランザクションと時計を使う
    private IRenderedComponent<TPage> Open<TPage>(params string[] roles) where TPage : IComponent => Screen(roles).Render<TPage>();

    private IRenderedComponent<ReceiptDetail> OpenDetail(long id, params string[] roles) =>
        Screen(roles).Render<ReceiptDetail>(p => p.Add(d => d.Id, id));

    private IRenderedComponent<InvoiceDetail> OpenInvoiceDetail(long id, params string[] roles) =>
        Screen(roles).Render<InvoiceDetail>(p => p.Add(d => d.Id, id));

    private BunitContext Screen(string[] roles)
    {
        var screen = new BunitContext();
        _screens.Add(screen);
        var user = TestUser.With(roles);
        screen.Services.AddSingleton<ICurrentUser>(user);
        screen.Services.AddSingleton(_clock);
        screen.Services.AddSingleton(Receipts(roles));
        screen.Services.AddSingleton(Invoices(roles));
        return screen;
    }

    private ReceiptUseCases Receipts(params string[] roles) => new(Sessions(TestUser.With(roles)), TestUser.With(roles), _clock);

    private InvoiceUseCases Invoices(params string[] roles) => new(Sessions(TestUser.With(roles)), _clock, TestUser.With(roles));

    // 選択肢が読み込まれてから操作する(最初の読み込みが終わってから)
    private static void WaitForOptions(IRenderedComponent<NewReceipt> page) =>
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("#billing-customer option[value=C001]")), Wait);

    // 行の文字を、空白を1つにそろえて読む
    private static string Text(AngleSharp.Dom.IElement row) =>
        string.Join(' ', row.QuerySelectorAll("td").Select(td => td.TextContent.Trim()).Where(t => t != ""));

    // 探してから入れるまでを、画面の描き直しと同じ流れで行う(QuotationPageTests と同じ)
    private static void Type<TPage>(IRenderedComponent<TPage> page, string selector, string value) where TPage : IComponent
    {
        page.InvokeAsync(() => page.Find(selector).Change(value)).GetAwaiter().GetResult();
        page.WaitForAssertion(() => Assert.Equal(value, page.Find(selector).GetAttribute("value") ?? ""), Wait);
    }
}
