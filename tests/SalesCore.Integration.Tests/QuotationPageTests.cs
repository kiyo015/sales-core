using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SalesCore.Application;
using SalesCore.Domain;
using SalesCore.Web.Components.Pages.Quotations;

namespace SalesCore.Integration.Tests;

/// <summary>
/// 見積の画面(bUnit)。ユースケースは本物で、DB はテストのトランザクションを使う。
/// 完了条件(Day54): 画面で見積を作って受注にできる。ボタンは状態・有効期限・役割で出し分け、URL を直接開いても拒否される。
/// 今日は日本時間の 2030-04-01 0:30(UTC ではまだ 3/31。本社 C001 の P001 に、4/1 から 90円の得意先別単価がある)。
/// </summary>
public class QuotationPageTests(DatabaseFixture fixture) : DatabaseTest(fixture), IDisposable
{
    private readonly BunitContext _screen = new();

    // ユースケースは本物の DB を使うので、描き直しを待つ時間を長めにする(SalesOrderPageTests と同じ)
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static readonly DateOnly Today = new(2030, 4, 1);
    private static readonly DateOnly ValidUntil = new(2030, 4, 30);
    private TimeProvider _clock = new FixedClock(new DateTimeOffset(2030, 4, 1, 0, 30, 0, TimeSpan.FromHours(9)));

    [Fact]
    public async Task 営業は見積を登録でき_見積日は今日が入り_単価は見積日の得意先別単価で_詳細の画面に移る()
    {
        await SeedCustomerPriceAsync();
        var page = Open<NewQuotation>(Roles.Sales);
        WaitForOptions(page);
        Assert.Equal("2030-04-01", page.Find("#quoted-on").GetAttribute("value"));
        Assert.Equal("", page.Find("#valid-until").GetAttribute("value") ?? ""); // 有効期限は既定値を持たない(2026-11-18 本人の決定)

        Type(page, "#customer", "C002"); // 支店。請求先(本社)の得意先別単価を使う
        Type(page, "#valid-until", "2030-04-30");
        SelectProduct(page, 0, "P001");
        page.WaitForAssertion(() => Assert.Equal("90.00", page.Find("#unit-price-0").GetAttribute("value")), Wait);
        Type(page, "#quantity-0", "3");
        page.Find("button.add-line").Click();
        SelectProduct(page, 1, "P002");
        Type(page, "#quantity-1", "2");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Matches(@"/quotations/\d+$", Navigation.Uri), Wait);
        var quotation = await Quotations(Roles.Sales).GetAsync(long.Parse(Navigation.Uri.Split('/')[^1]));
        Assert.Equal(("Draft", "C002", Today, ValidUntil), (quotation.Status, quotation.CustomerCode, quotation.QuotedOn, quotation.ValidUntil));
        Assert.Equal([("P001", 3m, 90m), ("P002", 2m, 33.33m)], quotation.Lines.Select(l => (l.ProductCode, l.Quantity, l.UnitPrice)));
    }

    // 単価は見積日で決まる(業務ルール集「見積と得意先別単価」)。見積日を変えると入れ直す(受注の画面と同じ LinePricing)
    [Fact]
    public async Task 見積日を変えると_その日の単価に入れ直す()
    {
        await SeedCustomerPriceAsync();
        var page = Open<NewQuotation>(Roles.Sales);
        WaitForOptions(page);
        Type(page, "#customer", "C001");
        SelectProduct(page, 0, "P001");
        page.WaitForAssertion(() => Assert.Equal("90.00", page.Find("#unit-price-0").GetAttribute("value")), Wait);

        Type(page, "#quoted-on", "2030-03-31");

        page.WaitForAssertion(() => Assert.Equal("105.00", page.Find("#unit-price-0").GetAttribute("value")), Wait); // 得意先別単価の初日の前日
    }

    // 有効期限は必ず入れる。省くと登録せず、ユースケースの理由を出す
    [Fact]
    public async Task 有効期限を入れずに登録すると_登録せずに理由を出す()
    {
        await SeedCustomerPriceAsync();
        var page = Open<NewQuotation>(Roles.Sales);
        WaitForOptions(page);
        Type(page, "#customer", "C001");
        SelectProduct(page, 0, "P001");
        Type(page, "#quantity-0", "1");
        var before = (await Quotations(Roles.Sales).ListAsync()).Count;

        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Contains("有効期限を入れる。", page.Find("[role=alert]").TextContent), Wait);
        Assert.Equal(before, (await Quotations(Roles.Sales).ListAsync()).Count); // 手元のテスト用 DB に別の行が残っていても比べられる形
    }

    // 作成中は明細を直して提出でき、提出済は受注にできる。受注にすると受注の詳細の画面に移る(営業の画面から受注まで通す)
    [Fact]
    public async Task 作成中の明細を直して提出し_受注にすると_受注の詳細の画面に移る()
    {
        var quotation = await SeedQuotationAsync();
        var page = OpenDetail(quotation.Id, Roles.Sales);
        page.WaitForAssertion(() => Assert.Equal("作成中", page.Find(".status").TextContent), Wait);
        Assert.Empty(page.FindAll("button.order")); // 作成中は受注にできない(先に提出する)

        var line = quotation.Lines[0].Id;
        Type(page, $"#quantity-{line}", "4");
        Type(page, $"#unit-price-{line}", "100");
        page.Find($"button.change-line-{line}").Click();
        page.WaitForAssertion(() => Assert.Equal("400 円", page.Find(".amount").TextContent), Wait);

        page.Find("button.submit").Click();
        page.WaitForAssertion(() => Assert.Equal("提出済", page.Find(".status").TextContent), Wait);
        Assert.Empty(page.FindAll($"#quantity-{line}")); // 提出済は直せない(新しい見積を作る)
        Assert.Empty(page.FindAll("button.submit"));

        page.Find("button.order").Click();

        page.WaitForAssertion(() => Assert.Matches(@"/orders/\d+$", Navigation.Uri), Wait);
        var order = await Orders(Roles.Sales).GetAsync(long.Parse(Navigation.Uri.Split('/')[^1]));
        Assert.Equal(("Received", Today), (order.Status, order.OrderedOn));
        Assert.Equal([("P001", 4m, 100m)], order.Lines.Select(l => (l.ProductCode, l.Quantity, l.UnitPrice)));
        Assert.Equal("Ordered", (await Quotations(Roles.Sales).GetAsync(quotation.Id)).Status);
    }

    // 単価を空にして直すと、0円にせずユースケースの理由を出す(PR #26 の AIレビュー2回目の Important と同じ)
    [Fact]
    public async Task 明細の単価を空にして直すと_直さずに理由を出す()
    {
        var quotation = await SeedQuotationAsync();
        var page = OpenDetail(quotation.Id, Roles.Sales);
        var line = quotation.Lines[0].Id;
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll($"#unit-price-{line}")), Wait);

        Type(page, $"#unit-price-{line}", "");
        page.Find($"button.change-line-{line}").Click();

        page.WaitForAssertion(() => Assert.Contains("単価を入れる。", page.Find("[role=alert]").TextContent), Wait);
        Assert.Equal(105m, (await Quotations(Roles.Sales).GetAsync(quotation.Id)).Lines.Single().UnitPrice);
    }

    // 有効期限を過ぎた提出済の見積は、状態は提出済のまま「期限切れ」と出し、受注にするボタンは出さない。失注は人が押す(2026-11-19 本人の決定)
    [Fact]
    public async Task 有効期限を過ぎた提出済の見積は_期限切れと出し_受注にするボタンを出さず_失注にできる()
    {
        var quotation = await SeedQuotationAsync();
        await Quotations(Roles.Sales).SubmitAsync(quotation.Id);
        _clock = new FixedClock(new DateTimeOffset(2030, 5, 1, 0, 0, 0, TimeSpan.FromHours(9))); // 有効期限(4/30)の翌日

        var page = OpenDetail(quotation.Id, Roles.Sales);

        page.WaitForAssertion(() => Assert.Equal("提出済（期限切れ）", page.Find(".status").TextContent), Wait);
        Assert.Empty(page.FindAll("button.order"));
        page.Find("button.lost").Click();
        page.WaitForAssertion(() => Assert.Equal("失注", page.Find(".status").TextContent), Wait);
        Assert.Empty(page.FindAll("button"));
    }

    // 期限切れの作成中の見積は提出できない(2026-11-18 本人の決定)。提出のボタンを出さない
    [Fact]
    public async Task 有効期限を過ぎた作成中の見積は_期限切れと出し_提出のボタンを出さない()
    {
        var quotation = await SeedQuotationAsync();
        _clock = new FixedClock(new DateTimeOffset(2030, 5, 1, 0, 0, 0, TimeSpan.FromHours(9)));

        var page = OpenDetail(quotation.Id, Roles.Sales);

        page.WaitForAssertion(() => Assert.Equal("作成中（期限切れ）", page.Find(".status").TextContent), Wait);
        Assert.Empty(page.FindAll("button.submit"));
        Assert.NotEmpty(page.FindAll("button.lost"));
    }

    // 一覧は全員が見られ、新しい順。期限切れも一覧で分かる。登録への入口は営業と管理者にだけ出す
    [Theory]
    [InlineData(Roles.Sales, true)]
    [InlineData(Roles.Admin, true)]
    [InlineData(Roles.Accounting, false)]
    public async Task 一覧は全員が見られ_登録の入口は営業と管理者だけに出す(string role, bool canRegister)
    {
        var expired = await SeedQuotationAsync(validUntil: Today.AddDays(-1));
        var draft = await SeedQuotationAsync();
        await Quotations(Roles.Sales).SubmitAsync(draft.Id);

        var page = Open<QuotationList>(role);

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll($"#quotation-{draft.Id}")), Wait);
        Assert.Equal([draft.Number, expired.Number], page.FindAll("tbody tr").Select(r => r.QuerySelector("a")!.TextContent).Where(n => n == draft.Number || n == expired.Number));
        Assert.Equal("提出済", page.Find($"#quotation-{draft.Id} .status").TextContent);
        Assert.Equal("作成中（期限切れ）", page.Find($"#quotation-{expired.Id} .status").TextContent);
        Assert.Equal(canRegister, page.FindAll("a.new-quotation").Count == 1);
    }

    // 経理は見積を見られるが、操作のボタンは出ない。登録の画面を URL で開いても拒む(入口と同じ表)
    [Fact]
    public async Task 経理は詳細を見られるがボタンは出ず_登録の画面は拒まれる()
    {
        var quotation = await SeedQuotationAsync();
        var detail = OpenDetail(quotation.Id, Roles.Accounting);
        detail.WaitForAssertion(() => Assert.Equal("作成中", detail.Find(".status").TextContent), Wait);
        Assert.Empty(detail.FindAll("button"));
        Assert.Empty(detail.FindAll("input"));

        var register = _screen.Render<NewQuotation>();

        register.WaitForAssertion(() => Assert.Equal("403", register.Find("[role=alert]").GetAttribute("data-status")), Wait);
        Assert.Empty(register.FindAll("form"));
    }

    [Fact]
    public async Task 無い見積を開くと404の理由を出す()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var page = OpenDetail(999_999_999, Roles.Sales);

        page.WaitForAssertion(() => Assert.Equal("404", page.Find("[role=alert]").GetAttribute("data-status")), Wait);
    }

    // 状態を足して表示名を書き忘れると、英語の名前(Draft など)がそのまま画面に出る
    [Fact]
    public void すべての状態に表示名がある()
    {
        Assert.Equal(Enum.GetNames<QuotationStatus>().Order(), QuotationStatusText.Labels.Keys.Order());
    }

    public void Dispose() => _screen.Dispose();

    private NavigationManager Navigation => _screen.Services.GetRequiredService<NavigationManager>();

    // 画面を開く。ユースケースは、その利用者で、このテストのトランザクションと時計を使う
    private IRenderedComponent<TPage> Open<TPage>(params string[] roles) where TPage : IComponent
    {
        Register(roles);
        return _screen.Render<TPage>();
    }

    private IRenderedComponent<QuotationDetail> OpenDetail(long id, params string[] roles)
    {
        Register(roles);
        return _screen.Render<QuotationDetail>(p => p.Add(d => d.Id, id));
    }

    private void Register(string[] roles)
    {
        var user = TestUser.With(roles);
        _screen.Services.AddSingleton<ICurrentUser>(user);
        _screen.Services.AddSingleton(_clock);
        _screen.Services.AddSingleton(Quotations(roles));
        _screen.Services.AddSingleton(Orders(roles));
        _screen.Services.AddSingleton(new MasterUseCases(Sessions(user), user));
    }

    private QuotationUseCases Quotations(params string[] roles) =>
        new(Sessions(TestUser.With(roles)), TestUser.With(roles), _clock, ApprovalThreshold.Default);

    private SalesOrderUseCases Orders(params string[] roles) =>
        new(Sessions(TestUser.With(roles)), TestUser.With(roles), ApprovalThreshold.Default);

    // 本社 C001 の P001 に、2030-04-01 から無期限で 90円(標準単価は 105円)
    private async Task SeedCustomerPriceAsync()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var admin = TestUser.With(Roles.Admin);
        await new MasterUseCases(Sessions(admin), admin)
            .RegisterCustomerPriceAsync(new RegisterCustomerPrice("C001", "P001", 90m, Today, null));
    }

    // 本社 C001 の、P001(105円)を1個の作成中の見積。見積日は有効期限か今日の早い方
    private async Task<QuotationView> SeedQuotationAsync(DateOnly? validUntil = null)
    {
        if (!await Db.Customers.AnyAsync(c => c.Code == "C001")) await new TestScenario(Db).SeedMastersAsync();
        var until = validUntil ?? ValidUntil;
        var quotedOn = until < Today ? until : Today;
        return await Quotations(Roles.Sales).CreateAsync(new CreateQuotation("C001", quotedOn, until, [new CreateQuotationLine("P001", 1m, 105m)]));
    }

    // 選択肢が読み込まれてから操作する(最初の読み込みが終わってから)
    private static void WaitForOptions(IRenderedComponent<NewQuotation> page) =>
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("#product-0 option[value=P001]")), Wait);

    // 商品を選ぶと、単価をユースケースに問い合わせて入れる(非同期)。入るまで待ってから次の操作をする
    private static void SelectProduct(IRenderedComponent<NewQuotation> page, int index, string productCode)
    {
        Type(page, $"#product-{index}", productCode);
        page.WaitForAssertion(() => Assert.False(string.IsNullOrEmpty(page.Find($"#unit-price-{index}").GetAttribute("value"))), Wait);
    }

    // 入力のたびに描き直されるので、要素は毎回探し直し、値が画面に入ったのを見てから次へ進む(SalesOrderPageTests と同じ)
    private static void Type<TPage>(IRenderedComponent<TPage> page, string selector, string value) where TPage : IComponent
    {
        page.Find(selector).Change(value);
        page.WaitForAssertion(() => Assert.Equal(value, page.Find(selector).GetAttribute("value") ?? ""), Wait);
    }
}
