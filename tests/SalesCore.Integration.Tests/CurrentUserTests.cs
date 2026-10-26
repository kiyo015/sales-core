using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SalesCore.Application;
using SalesCore.Infrastructure.Persistence;

namespace SalesCore.Integration.Tests;

/// <summary>
/// 作成者・更新者・監査ログの「操作した人」に、ログインした利用者の Id が入る(業務ルール集「8.」の「役割」)。
/// 業務の DbContext は、保存の時に <see cref="ICurrentUserSource"/> から利用者を読む。
/// </summary>
public class CurrentUserTests(DatabaseFixture fixture) : DatabaseTest(fixture)
{
    private const string UserId = "3f2a9c1e-0000-4000-8000-000000000001"; // AspNetUsers.Id の形(テスト用の値)

    [Fact]
    public async Task ログインした利用者で受注を登録すると_作成者と監査ログにその利用者のIdが入る()
    {
        await new TestScenario(Db).SeedMastersAsync();
        await using var session = SessionAs(new FixedUser(UserId));

        var order = await new SalesOrderUseCases(session).CreateAsync(
            new CreateSalesOrder("C001", new DateOnly(2026, 10, 26), [new CreateSalesOrderLine("P001", 2m)]));

        Assert.Equal(UserId, await CreatedByOfOrderAsync(order.Id));
        var auditUsers = await Db.Database
            .SqlQueryRaw<string?>("SELECT user_id AS \"Value\" FROM audit_logs WHERE entity_type = 'sales_orders' AND entity_id = {0}", order.Id)
            .ToListAsync();
        Assert.NotEmpty(auditUsers);
        Assert.All(auditUsers, u => Assert.Equal(UserId, u));
    }

    [Fact]
    public async Task ログインしていない操作では_作成者は空のまま()
    {
        await new TestScenario(Db).SeedMastersAsync();
        await using var session = SessionAs(new FixedUser(null));

        var order = await new SalesOrderUseCases(session).CreateAsync(
            new CreateSalesOrder("C001", new DateOnly(2026, 10, 26), [new CreateSalesOrderLine("P001", 2m)]));

        Assert.Null(await CreatedByOfOrderAsync(order.Id));
    }

    // 画面・API と同じ登録(AddSalesCore)で作った DbContext が、登録した利用者の出どころを使うこと。DB には繋がない
    [Fact]
    public void AddSalesCoreで作るDbContextは_登録した利用者の出どころを使う()
    {
        var services = new ServiceCollection();
        services.AddSalesCore("Host=localhost;Database=not_used_test");
        services.AddScoped<ICurrentUserSource>(_ => new FixedUser(UserId));
        using var scope = services.BuildServiceProvider().CreateScope();

        Assert.Equal(UserId, scope.ServiceProvider.GetRequiredService<SalesCoreDbContext>().CurrentUser);
    }

    private SalesCoreDbContext SessionAs(ICurrentUserSource user)
    {
        var session = new SalesCoreDbContext(
            new DbContextOptionsBuilder<SalesCoreDbContext>().UseNpgsql(Db.Database.GetDbConnection()).Options, user);
        JoinTransaction(session);
        return session;
    }

    private Task<string?> CreatedByOfOrderAsync(long id) => Db.Database
        .SqlQueryRaw<string?>("SELECT created_by AS \"Value\" FROM sales_orders WHERE id = {0}", id)
        .SingleAsync();

    private sealed class FixedUser(string? userId) : ICurrentUserSource
    {
        public string? UserId => userId;
    }
}
