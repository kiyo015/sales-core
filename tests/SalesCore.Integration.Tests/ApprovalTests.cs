using Microsoft.Extensions.Configuration;
using SalesCore.Application;
using SalesCore.Domain;

namespace SalesCore.Integration.Tests;

/// <summary>
/// 受注の承認の金額の基準(業務ルール集「承認」。本人の決定 2026-10-29)。
/// 税抜合計が基準額(既定100万円)以上なら登録の時から承認待ちで、承認待ちを承認できるのは管理者だけ。承認されるまで出荷できない。
/// </summary>
public class ApprovalTests(DatabaseFixture fixture) : DatabaseTest(fixture)
{
    private static readonly DateOnly Day = new(2026, 10, 29);
    private static readonly TestUser Sales = TestUser.With(Roles.Sales);
    private static readonly TestUser Admin = TestUser.With(Roles.Admin);

    // 境界の両側とちょうど × 営業・管理者。999,999円は税込(10%)で 1,099,999円 だが、税抜で比べるので営業が承認できる
    [Theory]
    [InlineData(999_999, Roles.Sales, "Received", true)]
    [InlineData(999_999, Roles.Admin, "Received", true)]
    [InlineData(1_000_000, Roles.Sales, "PendingApproval", false)]
    [InlineData(1_000_000, Roles.Admin, "PendingApproval", true)]
    [InlineData(1_000_001, Roles.Sales, "PendingApproval", false)]
    [InlineData(1_000_001, Roles.Admin, "PendingApproval", true)]
    public async Task 基準額以上は承認待ちで始まり_承認できるのは管理者だけ(int yen, string role, string registeredAs, bool canApprove)
    {
        var order = await RegisterAsync(yen);
        Assert.Equal(registeredAs, order.Status);

        var approve = () => UseCases(TestUser.With(role)).ApproveAsync(order.Id);

        if (canApprove)
        {
            Assert.Equal("Approved", (await approve()).Status);
        }
        else
        {
            await Assert.ThrowsAsync<ForbiddenException>(approve);
            Assert.Equal("PendingApproval", (await UseCases(Sales).GetAsync(order.Id)).Status); // 状態は変わらない
        }
    }

    [Fact]
    public async Task 承認待ちの受注は_管理者が承認するまで出荷を指示できない()
    {
        var order = await RegisterAsync(1_000_000);
        var instruct = () => UseCases(Sales).InstructShipmentAsync(order.Id, new InstructShipment("W01", [new(order.Lines[0].Id, 1m)]));

        await Assert.ThrowsAsync<BusinessRuleException>(instruct);
        await UseCases(Admin).ApproveAsync(order.Id);

        Assert.Equal(1m, (await instruct()).Lines.Single().Quantity);
    }

    // 承認しない時(却下)も、営業が取りやめる時も取消にする。取消できる人は「受注の取消」と同じ
    [Fact]
    public async Task 承認待ちの受注は_営業も取り消せる()
    {
        var order = await RegisterAsync(1_000_000);

        Assert.Equal("Cancelled", (await UseCases(Sales).CancelAsync(order.Id)).Status);
    }

    // 判定は登録の時の基準額。後で基準額を下げても、受付で登録した受注は営業が承認できる(承認の時に金額で判定し直さない)
    [Fact]
    public async Task 基準額を後で下げても_登録済みの受注の状態は変わらない()
    {
        var order = await RegisterAsync(1_500_000, new ApprovalThreshold(Money.Of(2_000_000m)));
        Assert.Equal("Received", order.Status);

        Assert.Equal("Approved", (await UseCases(Sales).ApproveAsync(order.Id)).Status); // 今の基準額(100万円)では承認待ちに当たる額
    }

    // 営業が登録する。明細1行で、単価を金額そのものにする(標準税率10%の商品)
    private async Task<SalesOrderView> RegisterAsync(int yen, ApprovalThreshold? threshold = null)
    {
        await new TestScenario(Db).SeedMastersAsync();
        return await UseCases(Sales, threshold).CreateAsync(new CreateSalesOrder("C001", Day, [new CreateSalesOrderLine("P001", 1m, yen)]));
    }

    private SalesOrderUseCases UseCases(TestUser user, ApprovalThreshold? threshold = null) =>
        new(Sessions(user), user, threshold ?? ApprovalThreshold.Default);
}

/// <summary>基準額の設定(SalesCore:ApprovalThreshold)の読み方。DB には繋がない。</summary>
public class ApprovalThresholdTests
{
    [Fact]
    public void 設定が無ければ100万円()
    {
        Assert.Equal(Money.Of(1_000_000m), ApprovalThreshold.From(Configuration(null)).Amount);
    }

    [Theory]
    [InlineData("500000", 500_000)]
    [InlineData("0", 0)] // すべての受注が承認待ちになる
    public void 設定があればその額(string value, int expected)
    {
        Assert.Equal(Money.Of(expected), ApprovalThreshold.From(Configuration(value)).Amount);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1000000.5")]
    public void 負の値や円未満の端数は設定の誤り(string value)
    {
        Assert.Throws<InvalidOperationException>(() => ApprovalThreshold.From(Configuration(value)));
    }

    private static IConfiguration Configuration(string? value) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [ApprovalThreshold.ConfigurationKey] = value }).Build();
}
