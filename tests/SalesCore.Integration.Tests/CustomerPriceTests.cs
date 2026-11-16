using Microsoft.EntityFrameworkCore;
using SalesCore.Application;
using SalesCore.Domain;

namespace SalesCore.Integration.Tests;

/// <summary>
/// 得意先別単価の登録・無効化と、受注の単価の決め方(業務ルール集「見積と得意先別単価」2026-11-13・11-16 本人の決定)。
/// 期間の境目(初日の前日・初日・末日・翌日)を、受注の登録を通して確かめる。重なりは Domain と DB の排他制約の両方で止める。
/// 日付は書いた日・実行する日と重ならない 2030 年にする(段階2の振り返り)。
/// </summary>
public class CustomerPriceTests(DatabaseFixture fixture) : DatabaseTest(fixture)
{
    private static readonly TestUser Admin = TestUser.With(Roles.Admin);

    private static DateOnly D(int month, int day) => new(2030, month, day);

    private MasterUseCases Masters() => new(Sessions(Admin), Admin);

    private SalesOrderUseCases Orders() => new(Sessions(Admin), Admin, ApprovalThreshold.Default);

    private async Task<decimal> OrderedUnitPriceAsync(string customerCode, DateOnly orderedOn, decimal? specified = null)
    {
        var order = await Orders().CreateAsync(new CreateSalesOrder(customerCode, orderedOn, [new CreateSalesOrderLine("P001", 1m, specified)]));
        return order.Lines.Single().UnitPrice;
    }

    [Fact]
    public async Task 改定すると_前の無期限の単価が前日で閉じて保存され_一覧に両方出る()
    {
        await new TestScenario(Db).SeedMastersAsync();
        await Masters().RegisterCustomerPriceAsync(new RegisterCustomerPrice("C001", "P001", 90m, D(4, 1), null));

        await Masters().RegisterCustomerPriceAsync(new RegisterCustomerPrice("C001", "P001", 80m, D(10, 1), null));

        var list = await Masters().ListCustomerPricesAsync();
        Assert.Equal(
            [("C001", "P001", 90m, D(4, 1), (DateOnly?)D(9, 30), true), ("C001", "P001", 80m, D(10, 1), null, true)],
            list.Select(p => (p.CustomerCode, p.ProductCode, p.UnitPrice, p.ValidFrom, p.ValidTo, p.IsActive)));
    }

    // 受注日の境目。標準単価は 105円、得意先別単価は 90円(4/1〜9/30)
    [Theory]
    [InlineData(3, 31, 105)]
    [InlineData(4, 1, 90)]
    [InlineData(9, 30, 90)]
    [InlineData(10, 1, 105)]
    public async Task 受注の単価は_受注日に有効な得意先別単価で_明細に写る(int month, int day, int expected)
    {
        await new TestScenario(Db).SeedMastersAsync();
        await Masters().RegisterCustomerPriceAsync(new RegisterCustomerPrice("C001", "P001", 90m, D(4, 1), D(9, 30)));

        Assert.Equal(expected, await OrderedUnitPriceAsync("C001", D(month, day)));
        Assert.Equal(expected, await Orders().UnitPriceForAsync("C001", "P001", D(month, day))); // 画面の初期値も同じ
    }

    [Fact]
    public async Task 支店の受注は本社の単価_明細で指定すればその単価()
    {
        await new TestScenario(Db).SeedMastersAsync();
        await Masters().RegisterCustomerPriceAsync(new RegisterCustomerPrice("C001", "P001", 90m, D(4, 1), null));

        Assert.Equal(90m, await OrderedUnitPriceAsync("C002", D(5, 1)));
        Assert.Equal(90m, await Orders().UnitPriceForAsync("C002", "P001", D(5, 1)));
        Assert.Equal(85m, await OrderedUnitPriceAsync("C002", D(5, 1), specified: 85m));
        Assert.Equal(105m, await OrderedUnitPriceAsync("C003", D(5, 1))); // 別の得意先には効かない
    }

    [Fact]
    public async Task 単価を改定しても_受注明細に写した単価は変わらない()
    {
        await new TestScenario(Db).SeedMastersAsync();
        await Masters().RegisterCustomerPriceAsync(new RegisterCustomerPrice("C001", "P001", 90m, D(4, 1), null));
        var order = await Orders().CreateAsync(new CreateSalesOrder("C001", D(10, 1), [new CreateSalesOrderLine("P001", 1m)]));

        await Masters().RegisterCustomerPriceAsync(new RegisterCustomerPrice("C001", "P001", 80m, D(10, 1), null));

        Assert.Equal(90m, (await Orders().GetAsync(order.Id)).Lines.Single().UnitPrice);
        Assert.Equal(80m, await OrderedUnitPriceAsync("C001", D(10, 1)));
    }

    [Fact]
    public async Task 無効にした単価は受注で使わず_同じ期間で登録し直せる()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var mistaken = await Masters().RegisterCustomerPriceAsync(new RegisterCustomerPrice("C001", "P001", 900m, D(4, 1), null));

        await Masters().InvalidateCustomerPriceAsync(mistaken.Id);
        Assert.Equal(105m, await OrderedUnitPriceAsync("C001", D(5, 1)));

        await Masters().RegisterCustomerPriceAsync(new RegisterCustomerPrice("C001", "P001", 90m, D(4, 1), null));
        Assert.Equal(90m, await OrderedUnitPriceAsync("C001", D(5, 1)));
    }

    [Theory]
    [InlineData("C002", "P001", "C001 に登録する")] // 支店には登録しない(本来の請求先を示す)
    [InlineData("NO-SUCH", "P001", "得意先コード NO-SUCH")]
    [InlineData("C001", "NO-SUCH", "商品コード NO-SUCH")]
    [InlineData("", "P001", "得意先を選ぶ")]
    [InlineData("C001", "", "商品を選ぶ")]
    public async Task 入力の誤りは_400の理由で拒む(string customerCode, string productCode, string expected)
    {
        await new TestScenario(Db).SeedMastersAsync();

        var error = await Assert.ThrowsAsync<InvalidInputException>(
            () => Masters().RegisterCustomerPriceAsync(new RegisterCustomerPrice(customerCode, productCode, 90m, D(4, 1), null)));

        Assert.Equal(400, OperationError.From(error)!.Status);
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task 期間を決めた単価と重なる登録は_409の理由で拒む()
    {
        await new TestScenario(Db).SeedMastersAsync();
        await Masters().RegisterCustomerPriceAsync(new RegisterCustomerPrice("C001", "P001", 90m, D(4, 1), D(9, 30)));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Masters().RegisterCustomerPriceAsync(new RegisterCustomerPrice("C001", "P001", 80m, D(9, 30), null)));

        Assert.Equal(409, OperationError.From(error)!.Status);
    }

    // Domain の確かめは、読んだ時点の単価しか見ない。2人が同時に登録した場面を、確かめを通った2つの単価を一度に保存して再現する
    [Fact]
    public async Task 同時に登録して期間が重なっても_DBの排他制約が止めて409にする()
    {
        var masters = await new TestScenario(Db).SeedMastersAsync();
        Db.AddRange(
            CustomerPrice.Register(masters.HeadOffice, masters.StandardProduct, 90m, D(4, 1), null, []),
            CustomerPrice.Register(masters.HeadOffice, masters.StandardProduct, 80m, D(10, 1), null, []));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => Db.SaveChangesAsync());

        Assert.Equal((409, true), (OperationError.From(error)!.Status, OperationError.From(error)!.Message.Contains("期間が重なる")));
    }

    [Fact]
    public async Task 無効の行はDBの排他制約でも重なりに数えない()
    {
        var masters = await new TestScenario(Db).SeedMastersAsync();
        var invalid = CustomerPrice.Register(masters.HeadOffice, masters.StandardProduct, 900m, D(4, 1), null, []);
        invalid.Invalidate();
        Db.AddRange(invalid, CustomerPrice.Register(masters.HeadOffice, masters.StandardProduct, 90m, D(4, 1), null, []));

        await Db.SaveChangesAsync();

        Assert.Equal(2, await Db.CustomerPrices.CountAsync());
    }
}
