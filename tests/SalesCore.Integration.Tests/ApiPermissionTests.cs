using System.Net;
using SalesCore.Application;

namespace SalesCore.Integration.Tests;

/// <summary>
/// 権限の検査で拒まれた時の HTTP の状態コード。未ログインは 401、役割が足りなければ 403。
/// ここではテストの利用者に差し替える。本当のログイン(トークン)で確かめる実効性テストは ApiAuthenticationTests。
/// </summary>
public class ApiPermissionTests(DatabaseFixture fixture) : ApiTest(fixture)
{
    [Fact]
    public async Task ログインしていなければ_401()
    {
        User = TestUser.Anonymous;

        await AssertProblem(Api(), "/sales-orders", HttpStatusCode.Unauthorized,
            new CreateSalesOrder("C002", new DateOnly(2026, 10, 1), [new CreateSalesOrderLine("P001", 1m)]));
    }

    [Fact]
    public async Task 経理が受注を登録しようとすると_403()
    {
        User = TestUser.With(Roles.Accounting);

        var problem = await AssertProblem(Api(), "/sales-orders", HttpStatusCode.Forbidden,
            new CreateSalesOrder("C002", new DateOnly(2026, 10, 1), [new CreateSalesOrderLine("P001", 1m)]));
        Assert.Contains("営業・管理者", problem.Detail);
    }
}
