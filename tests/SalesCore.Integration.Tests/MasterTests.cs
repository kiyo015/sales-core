using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SalesCore.Application;
using SalesCore.Domain;

namespace SalesCore.Integration.Tests;

/// <summary>
/// マスタ(得意先・商品・倉庫)の一覧と登録。参照は全員、登録は管理者だけ(役割の組み合わせは PermissionTests)。
/// 入力の誤りは 400、コードの重複は 409 で、画面に理由を出す(<see cref="OperationError"/>)。
/// </summary>
public class MasterTests(DatabaseFixture fixture) : DatabaseTest(fixture)
{
    private static readonly TestUser Admin = TestUser.With(Roles.Admin);

    [Fact]
    public async Task 得意先を登録すると_コード順の一覧に請求先と一緒に出る()
    {
        await new TestScenario(Db).SeedMastersAsync();

        var registered = await Masters().RegisterCustomerAsync(new RegisterCustomer("C010", "名古屋営業所", 31, "C001"));

        Assert.Equal(new CustomerView("C010", "名古屋営業所", 31, "C001", true), registered);
        var list = await Masters().ListCustomersAsync();
        Assert.Equal(["C001", "C002", "C003", "C010"], list.Select(c => c.Code));
        Assert.Equal("C001", list.Single(c => c.Code == "C002").BillingCustomerCode);
        Assert.Null(list.Single(c => c.Code == "C001").BillingCustomerCode);
    }

    [Fact]
    public async Task 商品は税区分をコードで選んで登録する()
    {
        await new TestScenario(Db).SeedMastersAsync();

        var registered = await Masters().RegisterProductAsync(new RegisterProduct("P010", "ナット", "袋", "RED", 12.5m));

        Assert.Equal(new ProductView("P010", "ナット", "袋", "RED", 12.5m, true), registered);
        Assert.Equal(["P001", "P002", "P010"], (await Masters().ListProductsAsync()).Select(p => p.Code));
        Assert.Equal(["RED", "STD"], (await Masters().ListTaxCategoriesAsync()).Select(t => t.Code));
    }

    [Fact]
    public async Task 倉庫を登録できる()
    {
        await new TestScenario(Db).SeedMastersAsync();

        await Masters().RegisterWarehouseAsync(new RegisterWarehouse("W02", "大阪倉庫"));

        Assert.Equal([new WarehouseView("W01", "本社倉庫", true), new WarehouseView("W02", "大阪倉庫", true)], await Masters().ListWarehousesAsync());
    }

    // 登録した得意先は、受注にそのまま使える(画面で作ったマスタで業務が回る)
    [Fact]
    public async Task 登録した得意先と商品で受注できる()
    {
        await new TestScenario(Db).SeedMastersAsync();
        await Masters().RegisterCustomerAsync(new RegisterCustomer("C010", "名古屋営業所", 31, "C001"));
        await Masters().RegisterProductAsync(new RegisterProduct("P010", "ナット", "袋", "STD", 12.5m));

        var order = await new SalesOrderUseCases(Sessions(Admin), Admin, ApprovalThreshold.Default)
            .CreateAsync(new CreateSalesOrder("C010", new DateOnly(2026, 10, 30), [new CreateSalesOrderLine("P010", 2m)]));

        Assert.Equal(25m, order.Amount);
    }

    [Theory]
    [InlineData("C001", "得意先コード C001 は既に登録されている")]
    [InlineData("C002", "得意先コード C002 は既に登録されている")]
    public async Task 同じコードの得意先は_409の理由で拒む(string code, string expected)
    {
        await new TestScenario(Db).SeedMastersAsync();

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Masters().RegisterCustomerAsync(new RegisterCustomer(code, "重複", 31)));

        Assert.Contains(expected, error.Message);
        Assert.Equal(409, OperationError.From(error)!.Status);
    }

    [Fact]
    public async Task 同じコードの商品と倉庫も拒む()
    {
        await new TestScenario(Db).SeedMastersAsync();

        var product = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Masters().RegisterProductAsync(new RegisterProduct("P001", "重複", "個", "STD", 1m)));
        var warehouse = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Masters().RegisterWarehouseAsync(new RegisterWarehouse("W01", "重複")));

        Assert.Contains("商品コード P001 は既に登録されている", product.Message);
        Assert.Contains("倉庫コード W01 は既に登録されている", warehouse.Message);
    }

    [Fact]
    public async Task 無いコードを指すと_400の理由で拒む()
    {
        await new TestScenario(Db).SeedMastersAsync();

        var billing = await Assert.ThrowsAsync<InvalidInputException>(
            () => Masters().RegisterCustomerAsync(new RegisterCustomer("C010", "名古屋営業所", 31, "NO-SUCH")));
        var tax = await Assert.ThrowsAsync<InvalidInputException>(
            () => Masters().RegisterProductAsync(new RegisterProduct("P010", "ナット", "袋", "NO-SUCH", 1m)));

        Assert.Contains("請求先", billing.Message);
        Assert.Contains("税区分", tax.Message);
        Assert.Equal(400, OperationError.From(billing)!.Status);
    }

    // 支店(C002)は本社(C001)を請求先に持つ。請求先は1段まで(本人の決定 2026-10-30)
    [Fact]
    public async Task 請求先を持つ得意先を請求先にすると_本来の請求先を示して拒む()
    {
        await new TestScenario(Db).SeedMastersAsync();

        var error = await Assert.ThrowsAsync<InvalidInputException>(
            () => Masters().RegisterCustomerAsync(new RegisterCustomer("C010", "堺営業所", 31, "C002")));

        Assert.Contains("C001 を指定する", error.Message);
    }

    // 画面の入力欄が空のまま送られた時(null)も、500 でなく理由を返す
    [Fact]
    public async Task 入力が空なら_400の理由で拒み_何も登録しない()
    {
        await new TestScenario(Db).SeedMastersAsync();

        var error = await Assert.ThrowsAsync<InvalidInputException>(() => Masters().RegisterCustomerAsync(new RegisterCustomer(null, null, 31)));

        Assert.Contains("得意先コードは空にできない", error.Message);
        Assert.Equal(3, (await Masters().ListCustomersAsync()).Count);
    }

    // 2人が同時に同じコードで登録すると、どちらも「まだ無い」と見て進み、保存の時に一意インデックスに当たる。
    // 保存の直前に、もう1人の得意先を割り込ませて再現する。制約ごとの理由で 409 にする(段階1からの持ち越し)
    [Fact]
    public async Task 同じコードを同時に登録すると_後の方は制約ごとの理由で409()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var racing = new MasterUseCases(new TestSessions(() => CreateAnotherSession(Admin, new InsertCustomerBeforeSave("C010"))), Admin);

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => racing.RegisterCustomerAsync(new RegisterCustomer("C010", "名古屋営業所", 31)));

        var problem = OperationError.From(error)!;
        Assert.Equal(409, problem.Status);
        Assert.Contains("得意先コード", problem.Message);
    }

    private MasterUseCases Masters() => new(Sessions(Admin), Admin);

    // 保存の直前に、同じコードの得意先を SQL で入れる(もう1人が先に登録した状態)
    private sealed class InsertCustomerBeforeSave(string code) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await eventData.Context!.Database.ExecuteSqlAsync(
                $"INSERT INTO customers (code, name, closing_day, is_active, row_version) VALUES ({code}, 'もう1人', 31, true, 0)", cancellationToken);
            return result;
        }
    }
}
