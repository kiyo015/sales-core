using Bunit;
using Microsoft.Extensions.DependencyInjection;
using SalesCore.Application;
using SalesCore.Web.Components.Pages.Masters;

namespace SalesCore.Integration.Tests;

/// <summary>
/// マスタの画面(bUnit)。ユースケースは本物で、DB はテストのトランザクションを使う。
/// 完了条件(Day41): 管理者だけがマスタを登録でき、入力の誤りと重複が画面に分かる文言で出る。
/// </summary>
public class MasterPageTests(DatabaseFixture fixture) : DatabaseTest(fixture), IDisposable
{
    private readonly BunitContext _screen = new();

    // ユースケースは本物の DB を使うので、描き直しを待つ時間を長めにする(既定の1秒では CI の Release で足りないことがあった。2026-10-30)
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task 管理者は得意先を登録でき_一覧に出る()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var page = Open<Customers>(Roles.Admin);

        Fill(page, "C010", "名古屋営業所");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Contains("得意先 C010 を登録しました。", page.Find("[role=status]").TextContent), Wait);
        Assert.Contains("名古屋営業所", page.Find("table").TextContent);
        Assert.Empty(page.FindAll("[role=alert]"));
    }

    [Fact]
    public async Task 同じコードで登録すると_重複の理由が出る()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var page = Open<Customers>(Roles.Admin);

        Fill(page, "C001", "重複");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Contains("得意先コード C001 は既に登録されている", page.Find("[role=alert]").TextContent), Wait);
        Assert.Equal("409", page.Find("[role=alert]").GetAttribute("data-status"));
        Assert.Contains("今の状態ではできません", page.Find("[role=alert]").TextContent);
    }

    [Fact]
    public async Task コードが空のまま登録すると_入力の誤りの理由が出る()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var page = Open<Customers>(Roles.Admin);

        Fill(page, "", "名古屋営業所");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Contains("得意先コードは空にできない", page.Find("[role=alert]").TextContent), Wait);
        Assert.Equal("400", page.Find("[role=alert]").GetAttribute("data-status"));
    }

    // 隠すのは使い勝手のため。営業が登録のユースケースを呼んでも入口で拒むことは PermissionTests が確かめる
    [Theory]
    [InlineData(Roles.Sales)]
    [InlineData(Roles.Accounting)]
    public async Task 管理者でなければ_登録の欄は出ず_一覧は見える(string role)
    {
        await new TestScenario(Db).SeedMastersAsync();
        var page = Open<Customers>(role);

        page.WaitForAssertion(() => Assert.Contains("本社", page.Find("table").TextContent), Wait);
        Assert.Empty(page.FindAll("form"));
    }

    [Fact]
    public async Task 役割の無い利用者には_権限が無い理由が出る()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var page = Open<Customers>();

        page.WaitForAssertion(() => Assert.Equal("403", page.Find("[role=alert]").GetAttribute("data-status")), Wait);
        Assert.Contains("この操作をする権限がありません", page.Find("[role=alert]").TextContent);
        Assert.DoesNotContain("本社", page.Find("table").TextContent);
    }

    // 請求先は1段まで(本人の決定 2026-10-30)。請求先を持つ支店(C002)は選択肢に出さない
    [Fact]
    public async Task 請求先の選択肢には_請求先を持たない得意先だけが出る()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var page = Open<Customers>(Roles.Admin);

        page.WaitForAssertion(() => Assert.Contains("C001", page.Find("table").TextContent), Wait);
        var options = page.FindAll("select")[1].QuerySelectorAll("option").Select(o => o.GetAttribute("value")).ToList();
        Assert.Equal(["", "C001", "C003"], options);
    }

    [Fact]
    public async Task 管理者は税区分を選んで商品を登録できる()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var page = Open<Products>(Roles.Admin);
        page.WaitForAssertion(() => Assert.Contains("P001", page.Find("table").TextContent), Wait);

        Type(page, 0, "P010");
        Type(page, 1, "ナット");
        Type(page, 2, "袋");
        page.Find("fieldset select").Change("RED");
        Type(page, 3, "12.5");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Contains("商品 P010 を登録しました。", page.Find("[role=status]").TextContent), Wait);
        Assert.Contains("ナット", page.Find("table").TextContent);
    }

    // 標準単価が数でなければ、ユースケースを呼ばずに入力欄で止める
    [Fact]
    public async Task 標準単価が数でなければ_登録せずに理由を出す()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var page = Open<Products>(Roles.Admin);
        page.WaitForAssertion(() => Assert.Contains("P001", page.Find("table").TextContent), Wait);

        Type(page, 0, "P010");
        Type(page, 3, "十円");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Contains("標準単価は数で入力してください。", page.Find(".validation-message").TextContent), Wait);
        Assert.Empty(page.FindAll("[role=status]"));
        Assert.Equal(2, (await Masters(Roles.Admin).ListProductsAsync()).Count);
    }

    [Fact]
    public async Task 管理者は倉庫を登録できる()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var page = Open<Warehouses>(Roles.Admin);

        Fill(page, "W02", "大阪倉庫");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Contains("倉庫 W02 を登録しました。", page.Find("[role=status]").TextContent), Wait);
        Assert.Contains("大阪倉庫", page.Find("table").TextContent);
    }

    public void Dispose() => _screen.Dispose();

    // 画面を開く。ユースケースは、その利用者で、このテストのトランザクションを使う
    private IRenderedComponent<TPage> Open<TPage>(params string[] roles) where TPage : Microsoft.AspNetCore.Components.IComponent
    {
        var user = TestUser.With(roles);
        _screen.Services.AddSingleton<ICurrentUser>(user);
        _screen.Services.AddSingleton(Masters(roles));
        return _screen.Render<TPage>();
    }

    private MasterUseCases Masters(params string[] roles) => new(Sessions(TestUser.With(roles)), TestUser.With(roles));

    // 一覧が出てから(最初の読み込みが終わってから)、コードと名前を入れる
    private static void Fill<TPage>(IRenderedComponent<TPage> page, string code, string name) where TPage : Microsoft.AspNetCore.Components.IComponent
    {
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("tbody tr")), Wait);
        Type(page, 0, code);
        Type(page, 1, name);
    }

    // 入力のたびに描き直されるので、要素は毎回探し直す(前の要素の操作の口は、描き直すと無くなる)
    // 入れた値が画面に入ったのを見てから次へ進む(待たずに次を入れると、入れた値が落ちることがあった。2026-10-30 の CI)
    private static void Type<TPage>(IRenderedComponent<TPage> page, int index, string value) where TPage : Microsoft.AspNetCore.Components.IComponent
    {
        page.FindAll("fieldset input")[index].Change(value);
        page.WaitForAssertion(() => Assert.Equal(value, page.FindAll("fieldset input")[index].GetAttribute("value") ?? ""), Wait);
    }
}
