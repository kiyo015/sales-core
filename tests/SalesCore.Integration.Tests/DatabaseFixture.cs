using Microsoft.EntityFrameworkCore;
using SalesCore.Infrastructure.Persistence;
using SalesCore.Infrastructure.Persistence.Identity;

namespace SalesCore.Integration.Tests;

/// <summary>
/// DBを使うテストはすべてこのコレクションに入れる。同じコレクションのテストは並列に走らないので、
/// 同じ表を触るテスト同士がぶつからない。
/// </summary>
[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "Database";
}

/// <summary>テスト用DBへの接続と、マイグレーションの適用(テスト実行ごとに1回)。</summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    public string ConnectionString { get; } = TestDatabase.LoadConnectionString();

    public SalesCoreDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<SalesCoreDbContext>().UseNpgsql(ConnectionString).Options);

    /// <summary>利用者と役割(Identity)の DbContext。業務の DbContext とは別で、マイグレーションの履歴の表も別。</summary>
    public SalesCoreIdentityDbContext CreateIdentityContext() =>
        new(new DbContextOptionsBuilder<SalesCoreIdentityDbContext>()
            .UseNpgsql(ConnectionString, npgsql => npgsql.MigrationsHistoryTable(SalesCoreIdentityDbContext.MigrationsHistoryTable))
            .Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        await using var identity = CreateIdentityContext();
        await identity.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
