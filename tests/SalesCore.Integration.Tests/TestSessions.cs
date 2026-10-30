using Microsoft.EntityFrameworkCore;
using SalesCore.Infrastructure.Persistence;

namespace SalesCore.Integration.Tests;

/// <summary>ユースケースに渡す DbContext の工場。作り方(テストのトランザクションに入れるなど)を渡す。</summary>
internal sealed class TestSessions(Func<SalesCoreDbContext> create) : IDbContextFactory<SalesCoreDbContext>
{
    public SalesCoreDbContext CreateDbContext() => create();
}
