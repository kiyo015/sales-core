using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SalesCore.Infrastructure.Persistence.Identity;

namespace SalesCore.Integration.Tests;

/// <summary>
/// CI のテスト用DBはまっさらで、マイグレーションを最初から順に当てる。手元のテスト用DBは適用済みなので、
/// 「最初から当てると失敗する」誤り(順序の誤り、手で足した SQL の誤り、拡張の不足など)は手元では見えない。
/// ここでは、まっさらなスキーマを作って全マイグレーションを最初から当て、最後にトランザクションごと取り消す。
/// </summary>
public class MigrationTests(DatabaseFixture fixture) : DatabaseTest(fixture)
{
    private const string Schema = "migration_probe";

    [Fact]
    public async Task 全マイグレーションをまっさらなスキーマに最初から適用できる()
    {
        var script = Db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.NoTransactions);

        // 表を作る先をこのスキーマにする。拡張(btree_gist)の型や演算子は public から見つける
        await Db.Database.ExecuteSqlRawAsync($"CREATE SCHEMA {Schema}; SET LOCAL search_path TO {Schema}, public;");
        await Db.Database.ExecuteSqlRawAsync(script);

        var applied = await Db.Database
            .SqlQueryRaw<string>($"SELECT \"MigrationId\" AS \"Value\" FROM {Schema}.\"__EFMigrationsHistory\" ORDER BY 1")
            .ToListAsync();
        Assert.Equal(Db.Database.GetMigrations(), applied);

        var tables = await Db.Database
            .SqlQueryRaw<string>($"SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = '{Schema}' ORDER BY 1")
            .ToListAsync();
        Assert.Equal(
            new[]
            {
                "__EFMigrationsHistory", "audit_logs", "customers", "invoice_tax_summaries", "invoices", "products", "sales_order_lines", "sales_orders",
                "sales_record_lines", "sales_records", "shipment_lines", "shipments", "tax_categories", "tax_rates", "warehouses",
            },
            tables);
    }

    // 障害のときに戻せるように、最後のマイグレーションの Down が通ることを確かめる(2026-10-15 の AIレビューで、
    // 既定値が使っているシーケンスを先に消そうとして失敗する Down を見つけた)。最初まで戻すと、ほかのスキーマの表が
    // 使っている拡張(btree_gist)まで消そうとするので、1つ前までにする
    [Fact]
    public async Task 最後のマイグレーションは1つ前に巻き戻せる()
    {
        var migrator = Db.GetService<IMigrator>();
        var migrations = Db.Database.GetMigrations().ToList();
        await Db.Database.ExecuteSqlRawAsync($"CREATE SCHEMA {Schema}; SET LOCAL search_path TO {Schema}, public;");
        await Db.Database.ExecuteSqlRawAsync(migrator.GenerateScript(options: MigrationsSqlGenerationOptions.NoTransactions));

        await Db.Database.ExecuteSqlRawAsync(migrator.GenerateScript(
            fromMigration: migrations[^1], toMigration: migrations[^2], options: MigrationsSqlGenerationOptions.NoTransactions));

        var applied = await Db.Database
            .SqlQueryRaw<string>($"SELECT \"MigrationId\" AS \"Value\" FROM {Schema}.\"__EFMigrationsHistory\" ORDER BY 1")
            .ToListAsync();
        Assert.Equal(migrations[..^1], applied);
    }

    // 利用者と役割(Identity)は別の DbContext で、マイグレーションの履歴の表も別(__identity_migrations_history)
    [Fact]
    public async Task Identityのマイグレーションも_まっさらなスキーマに最初から適用でき_巻き戻せる()
    {
        await using var identity = new SalesCoreIdentityDbContext(new DbContextOptionsBuilder<SalesCoreIdentityDbContext>()
            .UseNpgsql(Db.Database.GetDbConnection(), npgsql => npgsql.MigrationsHistoryTable(SalesCoreIdentityDbContext.MigrationsHistoryTable))
            .Options);
        JoinTransaction(identity);
        var migrator = identity.GetService<IMigrator>();
        var migrations = identity.Database.GetMigrations().ToList();
        await Db.Database.ExecuteSqlRawAsync($"CREATE SCHEMA {Schema}; SET LOCAL search_path TO {Schema}, public;");

        await Db.Database.ExecuteSqlRawAsync(migrator.GenerateScript(options: MigrationsSqlGenerationOptions.NoTransactions));

        var tables = await Db.Database
            .SqlQueryRaw<string>($"SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = '{Schema}' ORDER BY 1")
            .ToListAsync();
        Assert.Equal(
            new[]
            {
                "AspNetRoleClaims", "AspNetRoles", "AspNetUserClaims", "AspNetUserLogins", "AspNetUserRoles", "AspNetUserTokens", "AspNetUsers",
                "__identity_migrations_history",
            },
            tables);

        // 最初のマイグレーションしか無い間は、何も無い状態(0)まで戻す
        await Db.Database.ExecuteSqlRawAsync(migrator.GenerateScript(
            fromMigration: migrations[^1], toMigration: migrations.Count > 1 ? migrations[^2] : Migration.InitialDatabase,
            options: MigrationsSqlGenerationOptions.NoTransactions));
        var applied = await Db.Database
            .SqlQueryRaw<string>($"SELECT \"MigrationId\" AS \"Value\" FROM {Schema}.__identity_migrations_history ORDER BY 1")
            .ToListAsync();
        Assert.Equal(migrations[..^1], applied);
    }
}
