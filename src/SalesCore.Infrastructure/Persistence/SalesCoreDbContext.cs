using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SalesCore.Domain;

namespace SalesCore.Infrastructure.Persistence;

/// <summary>
/// 基幹システムのDB。表の定義は docs/domain/er-diagram.md(段階1の分)。表ごとの対応付けは <see cref="Configurations"/>。
/// 請求書(invoices)は Day31、全表に共通する列(作成者・更新日時・版数)は Day27 で足す。
/// </summary>
public sealed class SalesCoreDbContext(DbContextOptions<SalesCoreDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<TaxCategory> TaxCategories => Set<TaxCategory>();

    public DbSet<TaxRatePeriod> TaxRatePeriods => Set<TaxRatePeriod>();

    public DbSet<Warehouse> Warehouses => Set<Warehouse>();

    public DbSet<SalesOrder> SalesOrders => Set<SalesOrder>();

    public DbSet<Shipment> Shipments => Set<Shipment>();

    public DbSet<SalesRecord> SalesRecords => Set<SalesRecord>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // 金額は円単位(numeric(15,0))、税率は小数2桁(numeric(5,2))。業務ルール集「1. 金額と数量」
        configurationBuilder.Properties<Money>().HaveConversion<MoneyConverter>().HavePrecision(15, 0);
        configurationBuilder.Properties<TaxRate>().HaveConversion<TaxRateConverter>().HavePrecision(5, 2);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        Configurations.Apply(modelBuilder);

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }

            // 取引データは物理削除しない。親を消すと子が黙って消える ON DELETE CASCADE を作らせない
            foreach (var foreignKey in entity.GetForeignKeys())
            {
                foreignKey.DeleteBehavior = DeleteBehavior.Restrict;
            }
        }
    }

    // ShippedQuantity → shipped_quantity
    private static string ToSnakeCase(string name) => Regex.Replace(name, "(?<=[a-z0-9])([A-Z])", "_$1").ToLowerInvariant();

    private sealed class MoneyConverter() : ValueConverter<Money, decimal>(money => money.Yen, yen => Money.Of(yen));

    private sealed class TaxRateConverter() : ValueConverter<TaxRate, decimal>(rate => rate.Percent, percent => TaxRate.Of(percent));
}
