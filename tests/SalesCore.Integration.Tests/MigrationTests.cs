using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

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
                "__EFMigrationsHistory", "audit_logs", "customers", "products", "sales_order_lines", "sales_orders",
                "sales_record_lines", "sales_records", "shipment_lines", "shipments", "tax_categories", "tax_rates", "warehouses",
            },
            tables);
    }
}
