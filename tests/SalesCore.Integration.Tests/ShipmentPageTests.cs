using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SalesCore.Application;
using SalesCore.Domain;
using SalesCore.Web.Components.Pages.Orders;

namespace SalesCore.Integration.Tests;

/// <summary>
/// 出荷の画面(bUnit)。ユースケースは本物で、DB はテストのトランザクションを使う。
/// 完了条件(Day43): 画面から分納の出荷を指示・確定でき、受注数量を超える出荷が画面でも止まる。
/// 規則の定義元は業務ルール集「3. 販売の流れ」の「出荷と受注数量」。
/// </summary>
public class ShipmentPageTests(DatabaseFixture fixture) : DatabaseTest(fixture), IDisposable
{
    private readonly BunitContext _screen = new();

    // ユースケースは本物の DB を使うので、描き直しを待つ時間を長めにする(MasterPageTests と同じ)
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static readonly DateOnly Day = new(2026, 11, 2);

    // ── 指示 ──
    // 分納: 明細ごとに今回の数量を入れる。空にした明細は今回は出さない
    [Fact]
    public async Task 営業は承認済の受注の一部を出荷指示でき_受注の画面に移る()
    {
        var (order, first, second) = await SeedApprovedOrderAsync(10m, 5m);
        var page = OpenNew(order.Id, Roles.Sales);
        WaitForForm(page);

        Type(page, "#warehouse", "W01");
        Type(page, $"#quantity-{first}", "4");
        Type(page, $"#quantity-{second}", "");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.EndsWith($"/orders/{order.Id}", Navigation.Uri), Wait);
        var shipment = Assert.Single(await Orders(Roles.Sales).ListShipmentsAsync(order.Id));
        Assert.Equal("Instructed", shipment.Status);
        Assert.Equal([(first, 4m)], shipment.Lines.Select(l => (l.OrderLineId, l.Quantity)));
    }

    // 今回の数量の初期値は残り(全部を出す時は倉庫を選ぶだけ)。残りが0の明細は出さない
    [Fact]
    public async Task 今回の数量の初期値は残りで_出荷を終えた明細は出ない()
    {
        var (order, first, second) = await SeedApprovedOrderAsync(10m, 5m);
        await ShipAsync(order.Id, (second, 5m));
        var page = OpenNew(order.Id, Roles.Sales);
        WaitForForm(page);

        Assert.Equal("10", page.Find($"#quantity-{first}").GetAttribute("value"));
        Assert.Empty(page.FindAll($"#quantity-{second}"));
    }

    [Fact]
    public async Task 残りより多い数量を指示すると_理由が出て_指示しない()
    {
        var (order, first, _) = await SeedApprovedOrderAsync(10m, 5m);
        var page = OpenNew(order.Id, Roles.Sales);
        WaitForForm(page);

        Type(page, "#warehouse", "W01");
        Type(page, $"#quantity-{first}", "11");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Contains("受注数量を超える出荷はできない", page.Find("[role=alert]").TextContent), Wait);
        Assert.Equal("409", page.Find("[role=alert]").GetAttribute("data-status"));
        Assert.Empty(await Orders(Roles.Sales).ListShipmentsAsync(order.Id));
    }

    [Fact]
    public async Task 倉庫を選ばずに指示すると_選ぶよう求める()
    {
        var (order, _, _) = await SeedApprovedOrderAsync(10m, 5m);
        var page = OpenNew(order.Id, Roles.Sales);
        WaitForForm(page);

        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Contains("倉庫を選ぶ", page.Find("[role=alert]").TextContent), Wait);
        Assert.Equal("400", page.Find("[role=alert]").GetAttribute("data-status"));
    }

    // 無効にした倉庫からは出荷を指示できない(ユースケースも拒む)。選択肢に出さない
    [Fact]
    public async Task 無効にした倉庫は_選択肢に出ない()
    {
        var (order, _, _) = await SeedApprovedOrderAsync(10m, 5m);
        Db.Add(new Warehouse("W02", "旧倉庫"));
        await Db.SaveChangesAsync();
        await Db.Database.ExecuteSqlAsync($"UPDATE warehouses SET is_active = false WHERE code = 'W02'");
        var page = OpenNew(order.Id, Roles.Sales);
        WaitForForm(page);

        Assert.Equal(["", "W01"], page.FindAll("#warehouse option").Select(o => o.GetAttribute("value")));
    }

    // URL を直接開いても、入口と同じ表で拒む
    [Fact]
    public async Task 経理が出荷の指示の画面を開くと_権限が無い理由が出て_入力欄は出ない()
    {
        var (order, _, _) = await SeedApprovedOrderAsync(10m, 5m);
        var page = OpenNew(order.Id, Roles.Accounting);

        page.WaitForAssertion(() => Assert.Equal("403", page.Find("[role=alert]").GetAttribute("data-status")), Wait);
        Assert.Empty(page.FindAll("form"));
    }

    // ── 受注の画面の、出荷の入口と一覧 ──
    [Theory]
    [InlineData(Roles.Sales, true)]
    [InlineData(Roles.Admin, true)]
    [InlineData(Roles.Accounting, false)]
    public async Task 承認済の受注の出荷の指示への入口は_営業と管理者にだけ出る(string role, bool canInstruct)
    {
        var (order, _, _) = await SeedApprovedOrderAsync(10m, 5m);
        var page = OpenDetail(order.Id, role);
        page.WaitForAssertion(() => Assert.Contains("承認済", page.Find(".status").TextContent), Wait);

        Assert.Equal(canInstruct, page.FindAll($"a.new-shipment[href='orders/{order.Id}/shipments/new']").Count == 1);
    }

    // 出荷できるのは承認済と一部出荷だけ(業務ルール集「状態ごとにできる操作」)
    [Fact]
    public async Task 承認前の受注には_出荷の指示への入口が出ない()
    {
        var scenario = new TestScenario(Db);
        var m = await scenario.SeedMastersAsync();
        var order = scenario.AddOrder(m.HeadOffice, Day, (m.StandardProduct, 10m, TaxRate.Of(10m)));
        await Db.SaveChangesAsync();
        var page = OpenDetail(order.Id, Roles.Sales);
        page.WaitForAssertion(() => Assert.Contains("受付", page.Find(".status").TextContent), Wait);

        Assert.Empty(page.FindAll("a.new-shipment"));
    }

    // ── 確定と取消 ──
    [Fact]
    public async Task 指示した出荷を確定すると_残りが減って一部出荷になり_売上の番号が出る()
    {
        var (order, first, _) = await SeedApprovedOrderAsync(10m, 5m);
        var shipment = await ShipmentAsync(order.Id, (first, 4m));
        var page = OpenDetail(order.Id, Roles.Sales);
        WaitForShipment(page, shipment.Id);

        Type(page, $"#shipped-on-{shipment.Id}", "2026-11-04");
        page.Find($"#shipment-{shipment.Id} button.confirm-shipment").Click();

        page.WaitForAssertion(() => Assert.Contains("一部出荷", page.Find(".status").TextContent), Wait);
        Assert.Contains($"出荷 {shipment.Number} を確定しました", page.Find("[role=status]").TextContent);
        Assert.Equal("6", page.Find($"#line-{first} .remaining").TextContent);
        var row = page.Find($"#shipment-{shipment.Id}");
        Assert.Contains("確定", row.QuerySelector(".shipment-status")!.TextContent);
        Assert.Contains("2026-11-04", row.TextContent);
        Assert.Matches(@"SR-\d{6}", row.TextContent);
        Assert.Empty(page.FindAll($"#shipment-{shipment.Id} button"));
    }

    // 分納: 2回に分けて出し、すべての明細で残りが0になれば出荷済
    [Fact]
    public async Task 分けて指示した出荷をすべて確定すると_出荷済になり_指示への入口が消える()
    {
        var (order, first, second) = await SeedApprovedOrderAsync(10m, 5m);
        var earlier = await ShipmentAsync(order.Id, (first, 4m), (second, 5m));
        var later = await ShipmentAsync(order.Id, (first, 6m));
        var page = OpenDetail(order.Id, Roles.Sales);
        WaitForShipment(page, later.Id);

        page.Find($"#shipment-{earlier.Id} button.confirm-shipment").Click();
        page.WaitForAssertion(() => Assert.Contains("一部出荷", page.Find(".status").TextContent), Wait);
        page.Find($"#shipment-{later.Id} button.confirm-shipment").Click();

        page.WaitForAssertion(() => Assert.Contains("出荷済", page.Find(".status").TextContent), Wait);
        Assert.Empty(page.FindAll("a.new-shipment"));
    }

    // 受注数量を超えないことは、確定の時にも確かめる(指示の後に別の出荷が先に確定することがある)
    [Fact]
    public async Task 開いている間に別の出荷が先に確定すると_確定は理由付きで止まり_今の状態を出す()
    {
        var (order, first, _) = await SeedApprovedOrderAsync(10m, 5m);
        var mine = await ShipmentAsync(order.Id, (first, 10m));
        var other = await ShipmentAsync(order.Id, (first, 10m));
        var page = OpenDetail(order.Id, Roles.Sales);
        WaitForShipment(page, mine.Id);
        await Orders(Roles.Sales).ConfirmShipmentAsync(other.Id, new ConfirmShipment(Day));

        page.Find($"#shipment-{mine.Id} button.confirm-shipment").Click();

        page.WaitForAssertion(() => Assert.Contains("受注数量を超える出荷はできない", page.Find("[role=alert]").TextContent), Wait);
        Assert.Equal("409", page.Find("[role=alert]").GetAttribute("data-status"));
        Assert.Equal("0", page.Find($"#line-{first} .remaining").TextContent); // 読み直した状態
        Assert.Contains("指示", page.Find($"#shipment-{mine.Id} .shipment-status").TextContent);
    }

    [Fact]
    public async Task 受注日より前の出荷日では確定できず_理由が出る()
    {
        var (order, first, _) = await SeedApprovedOrderAsync(10m, 5m);
        var shipment = await ShipmentAsync(order.Id, (first, 4m));
        var page = OpenDetail(order.Id, Roles.Sales);
        WaitForShipment(page, shipment.Id);

        Type(page, $"#shipped-on-{shipment.Id}", "2026-11-01");
        page.Find($"#shipment-{shipment.Id} button.confirm-shipment").Click();

        page.WaitForAssertion(() => Assert.Contains("受注日", page.Find("[role=alert]").TextContent), Wait);
        Assert.Equal("400", page.Find("[role=alert]").GetAttribute("data-status"));
        Assert.Equal("Instructed", (await Orders(Roles.Sales).GetShipmentAsync(shipment.Id)).Status);
    }

    // 出荷日の初期値は日本時間の今日(受注日と同じ。PR #15 の AIレビュー)
    [Fact]
    public async Task 出荷日の初期値は_サーバーがUTCでも日本時間の今日()
    {
        var (order, first, _) = await SeedApprovedOrderAsync(10m, 5m);
        var shipment = await ShipmentAsync(order.Id, (first, 4m));
        _screen.Services.AddSingleton<TimeProvider>(new FixedClock(new DateTimeOffset(2026, 11, 9, 16, 0, 0, TimeSpan.Zero))); // 日本時間 11/10 1:00(実行する日と重ならない日にする)
        var page = OpenDetail(order.Id, Roles.Sales);
        WaitForShipment(page, shipment.Id);

        Assert.Equal("2026-11-10", page.Find($"#shipped-on-{shipment.Id}").GetAttribute("value"));
    }

    [Fact]
    public async Task 指示した出荷を取り消すと_取消になり_残りは変わらない()
    {
        var (order, first, _) = await SeedApprovedOrderAsync(10m, 5m);
        var shipment = await ShipmentAsync(order.Id, (first, 4m));
        var page = OpenDetail(order.Id, Roles.Sales);
        WaitForShipment(page, shipment.Id);

        page.Find($"#shipment-{shipment.Id} button.cancel-shipment").Click();

        page.WaitForAssertion(() => Assert.Contains("取消", page.Find($"#shipment-{shipment.Id} .shipment-status").TextContent), Wait);
        Assert.Contains($"出荷 {shipment.Number} を取り消しました", page.Find("[role=status]").TextContent);
        Assert.Equal("10", page.Find($"#line-{first} .remaining").TextContent);
        Assert.Contains("承認済", page.Find(".status").TextContent);
        Assert.Empty(page.FindAll($"#shipment-{shipment.Id} button"));
    }

    // 受注を取り消すと、出ていた指示は確定できない。指示の取消はできる(片付けられるように)
    [Fact]
    public async Task 取り消した受注の出荷の指示には_確定のボタンが出ず_取消のボタンは出る()
    {
        var (order, first, _) = await SeedApprovedOrderAsync(10m, 5m);
        var shipment = await ShipmentAsync(order.Id, (first, 4m));
        await Orders(Roles.Sales).CancelAsync(order.Id);
        var page = OpenDetail(order.Id, Roles.Sales);
        WaitForShipment(page, shipment.Id);

        Assert.Empty(page.FindAll($"#shipment-{shipment.Id} button.confirm-shipment"));
        Assert.Single(page.FindAll($"#shipment-{shipment.Id} button.cancel-shipment"));
    }

    [Fact]
    public async Task 経理には出荷が見え_確定と取消のボタンは出ない()
    {
        var (order, first, _) = await SeedApprovedOrderAsync(10m, 5m);
        var shipment = await ShipmentAsync(order.Id, (first, 4m));
        var page = OpenDetail(order.Id, Roles.Accounting);
        WaitForShipment(page, shipment.Id);

        Assert.Contains("指示", page.Find($"#shipment-{shipment.Id} .shipment-status").TextContent);
        Assert.Empty(page.FindAll($"#shipment-{shipment.Id} button"));
        Assert.Empty(page.FindAll($"#shipped-on-{shipment.Id}"));
    }

    // 状態を足して表示名を書き忘れると、英語の名前(Instructed など)がそのまま画面に出る
    [Fact]
    public void すべての出荷の状態に表示名がある()
    {
        Assert.Equal(Enum.GetNames<ShipmentStatus>().Order(), ShipmentStatusText.Labels.Keys.Order());
    }

    public void Dispose() => _screen.Dispose();

    private NavigationManager Navigation => _screen.Services.GetRequiredService<NavigationManager>();

    private IRenderedComponent<NewShipment> OpenNew(long orderId, params string[] roles)
    {
        Register(roles);
        return _screen.Render<NewShipment>(p => p.Add(s => s.OrderId, orderId));
    }

    private IRenderedComponent<SalesOrderDetail> OpenDetail(long id, params string[] roles)
    {
        Register(roles);
        return _screen.Render<SalesOrderDetail>(p => p.Add(d => d.Id, id));
    }

    // ユースケースは、その利用者で、このテストのトランザクションを使う
    private void Register(string[] roles)
    {
        var user = TestUser.With(roles);
        _screen.Services.AddSingleton<ICurrentUser>(user);
        _screen.Services.TryAddSingleton(TimeProvider.System); // 時計を決めたテストは、先に登録しておく
        _screen.Services.AddSingleton(Orders(roles));
        _screen.Services.AddSingleton(new MasterUseCases(Sessions(user), user));
    }

    private SalesOrderUseCases Orders(params string[] roles) =>
        new(Sessions(TestUser.With(roles)), TestUser.With(roles), ApprovalThreshold.Default);

    // 得意先 C001 に、P001(105円)と P002(33.33円)の2行。承認済。明細の id を返す
    private async Task<(SalesOrder Order, long First, long Second)> SeedApprovedOrderAsync(decimal first, decimal second)
    {
        var scenario = new TestScenario(Db);
        var m = await scenario.SeedMastersAsync();
        var order = scenario.AddOrder(m.HeadOffice, Day, (m.StandardProduct, first, TaxRate.Of(10m)), (m.ReducedProduct, second, TaxRate.Of(8m)));
        order.Approve();
        await Db.SaveChangesAsync();
        return (order, order.Lines[0].Id, order.Lines[1].Id);
    }

    private Task<ShipmentView> ShipmentAsync(long orderId, params (long LineId, decimal Quantity)[] lines) =>
        Orders(Roles.Sales).InstructShipmentAsync(orderId, new InstructShipment("W01", lines.Select(l => new ShipmentLineRequest(l.LineId, l.Quantity)).ToList()));

    private async Task ShipAsync(long orderId, params (long LineId, decimal Quantity)[] lines) =>
        await Orders(Roles.Sales).ConfirmShipmentAsync((await ShipmentAsync(orderId, lines)).Id, new ConfirmShipment(Day));

    // 倉庫の選択肢が読み込まれてから操作する
    private static void WaitForForm(IRenderedComponent<NewShipment> page) =>
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("#warehouse option[value=W01]")), Wait);

    private static void WaitForShipment(IRenderedComponent<SalesOrderDetail> page, long shipmentId) =>
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll($"#shipment-{shipmentId}")), Wait);

    // 入力のたびに描き直されるので、要素は毎回探し直し、値が画面に入ったのを見てから次へ進む(MasterPageTests と同じ)
    private static void Type<TPage>(IRenderedComponent<TPage> page, string selector, string value) where TPage : IComponent
    {
        page.Find(selector).Change(value);
        page.WaitForAssertion(() => Assert.Equal(value, page.Find(selector).GetAttribute("value") ?? ""), Wait);
    }
}
