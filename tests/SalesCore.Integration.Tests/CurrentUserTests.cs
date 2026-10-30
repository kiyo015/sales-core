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
        var sessions = new TestSessions(() => SessionAs(new FixedUser(UserId)));

        var order = await new SalesOrderUseCases(sessions, TestUser.With(Roles.Sales), ApprovalThreshold.Default).CreateAsync(
            new CreateSalesOrder("C001", new DateOnly(2026, 10, 26), [new CreateSalesOrderLine("P001", 2m)]));

        Assert.Equal(UserId, await CreatedByOfOrderAsync(order.Id));
        var auditUsers = await Db.Database
            .SqlQueryRaw<string?>("SELECT user_id AS \"Value\" FROM audit_logs WHERE entity_type = 'sales_orders' AND entity_id = {0}", order.Id)
            .ToListAsync();
        Assert.NotEmpty(auditUsers);
        Assert.All(auditUsers, u => Assert.Equal(UserId, u));
    }

    // 業務の操作では空にならない(権限の検査と操作した人は、本番では同じ LoggedInUser から来る)。
    // これは起動時の処理など、操作した人のいない保存に当たる。出どころが null なら作成者も null で、別の値で埋めない
    [Fact]
    public async Task 操作した人のいない保存では_作成者は空のまま()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var sessions = new TestSessions(() => SessionAs(new FixedUser(null)));

        var order = await new SalesOrderUseCases(sessions, TestUser.With(Roles.Sales), ApprovalThreshold.Default).CreateAsync(
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

        // ユースケースは工場から操作ごとに作る(本人の決定 2026-10-30)。工場が作る DbContext にも、同じ出どころが渡ること
        using var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<SalesCoreDbContext>>().CreateDbContext();
        Assert.Equal(UserId, db.CurrentUser);
    }

    // 画面は利用者ごと(回線ごと)にスコープがある。工場を全員で共有すると、DbContext に別の利用者(最初に作った時の利用者)が入る
    // (対照実験 2026-10-30: 工場を singleton にしても、上の利用者が1人のテストは通った)
    [Fact]
    public void AddSalesCoreの工場が作るDbContextは_スコープごとの利用者を使う()
    {
        var services = new ServiceCollection();
        services.AddSalesCore("Host=localhost;Database=not_used_test");
        var next = 0;
        services.AddScoped<ICurrentUserSource>(_ => new FixedUser($"user-{++next}"));
        var provider = services.BuildServiceProvider();

        foreach (var _ in Enumerable.Range(0, 2))
        {
            using var scope = provider.CreateScope();
            using var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<SalesCoreDbContext>>().CreateDbContext();
            Assert.Equal(scope.ServiceProvider.GetRequiredService<ICurrentUserSource>().UserId, db.CurrentUser);
        }
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
