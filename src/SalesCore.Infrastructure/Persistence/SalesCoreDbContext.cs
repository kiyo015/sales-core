using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SalesCore.Domain;

namespace SalesCore.Infrastructure.Persistence;

/// <summary>
/// 基幹システムのDB。表の定義は docs/domain/er-diagram.md(段階1の分)。表ごとの対応付けは <see cref="Configurations"/>。
/// 請求書(invoices)は Day31 で足す。
/// 保存のときに、業務ルール集「7. データの扱い」を守る:
/// 物理削除を止める、作成者・更新日時・版数を入れる、変えた値を監査ログに残す(<see cref="SaveChangesAsync(bool, CancellationToken)"/>)。
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

    /// <summary>操作した人。ユーザーを作る段階2までは無い(null)。</summary>
    public string? CurrentUser { get; set; }

    /// <summary>作成日時・更新日時・監査ログの日時の出どころ。テストでは固定の時刻に差し替える。</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        SaveChangesAsync(acceptAllChangesOnSuccess).GetAwaiter().GetResult();

    /// <summary>
    /// 業務のデータと監査ログを、1つのトランザクションで保存する(呼び出し側がトランザクションを始めていれば、それに乗る)。
    /// 監査ログには表と行の id が要り、新しい行の id は保存するまで決まらないので、業務のデータを保存してから監査ログを保存する。
    /// </summary>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        if (!acceptAllChangesOnSuccess)
        {
            // 監査ログを2回目の保存で書くので、1回目で変更を確定させる必要がある
            throw new NotSupportedException("acceptAllChangesOnSuccess: false には対応していない。");
        }

        ChangeTracker.DetectChanges();
        RejectDeletes();
        TouchAggregateRoots();

        var now = Clock.GetUtcNow();
        var audits = ChangeTracker.Entries()
            .Where(e => e.Entity is not AuditLog && e.State is EntityState.Added or EntityState.Modified)
            .Select(e => PendingAudit.Capture(e))
            .ToList();
        foreach (var audit in audits)
        {
            Stamp(audit.Entry, now);
        }

        await using var transaction = Database.CurrentTransaction is null ? await Database.BeginTransactionAsync(cancellationToken) : null;
        var saved = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        AuditLogs.AddRange(audits.Where(a => a.HasBusinessChange).Select(a => a.ToLog(now, CurrentUser)));
        await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        return saved;
    }

    // 取消は状態か赤黒で表す。子のある親は外部キー(RESTRICT)でも止まるが、子の無い行(明細など)はここでしか止まらない
    private void RejectDeletes()
    {
        var deleted = ChangeTracker.Entries().FirstOrDefault(e => e.State == EntityState.Deleted);
        if (deleted is not null)
        {
            throw new InvalidOperationException(
                $"物理削除はしない({deleted.Metadata.GetTableName()})。取消は状態か赤黒で表す(業務ルール集「7. データの扱い」)。");
        }
    }

    // 受注明細(出荷済み数量など)が変わったら、受注の版数も上げる。同じ受注の別々の明細を2人が同時に出荷すると、
    // どちらも「ほかの行が残っている」と見て受注の状態を変えないことがあり、明細だけでは衝突に気付けないため
    private void TouchAggregateRoots()
    {
        var changedOrderIds = ChangeTracker.Entries<SalesOrderLine>()
            .Where(e => e.State == EntityState.Modified)
            .Select(e => e.Property<long>("SalesOrderId").CurrentValue)
            .ToHashSet();
        foreach (var order in ChangeTracker.Entries<SalesOrder>().Where(e => changedOrderIds.Contains(e.Entity.Id)))
        {
            order.Property(Configurations.RowVersion).IsModified = true;
        }
    }

    private void Stamp(EntityEntry entry, DateTimeOffset now)
    {
        if (entry.State == EntityState.Added)
        {
            entry.Property(Configurations.CreatedAt).CurrentValue = now;
            entry.Property(Configurations.CreatedBy).CurrentValue = CurrentUser;
        }
        else
        {
            var version = entry.Property(Configurations.RowVersion);
            version.CurrentValue = (int)version.OriginalValue! + 1; // 元の値は、保存のときに WHERE row_version = 元の値 で比べる
        }
        entry.Property(Configurations.UpdatedAt).CurrentValue = now;
        entry.Property(Configurations.UpdatedBy).CurrentValue = CurrentUser;
    }

    /// <summary>保存の前に、どの値を残すかと変更前の値を控えておく。変更後の値と新しい行の id は保存した後に読む。</summary>
    private sealed record PendingAudit(EntityEntry Entry, string Action, IReadOnlyList<IProperty> Properties, string? BeforeValues)
    {
        public bool HasBusinessChange => Properties.Count > 0;

        public static PendingAudit Capture(EntityEntry entry)
        {
            var added = entry.State == EntityState.Added;
            // 業務の値だけを残す。共通の列と id は除く。変更なら、変わった列だけ
            var properties = entry.Properties
                .Where(p => !p.Metadata.IsPrimaryKey() && !Configurations.CommonColumns.Contains(p.Metadata.Name) && (added || p.IsModified))
                .Select(p => p.Metadata)
                .ToList();
            return new PendingAudit(
                entry,
                added ? "created" : "updated",
                properties,
                added ? null : Snapshot(properties, p => entry.OriginalValues[p]));
        }

        public AuditLog ToLog(DateTimeOffset occurredAt, string? user) => new()
        {
            UserId = user,
            EntityType = Entry.Metadata.GetTableName()!,
            EntityId = (long)Entry.Property("Id").CurrentValue!,
            Action = Action,
            BeforeValues = BeforeValues,
            AfterValues = Snapshot(Properties, p => Entry.CurrentValues[p]),
            OccurredAt = occurredAt,
        };

        // 列名 → DB に入る形の値(Money は円の数、状態は文字列)。
        // 変換器は型の対応付けから取る(HasConversion<string>() は DB での型を決めるだけで、GetValueConverter() では取れない)
        private static string Snapshot(IReadOnlyList<IProperty> properties, Func<IProperty, object?> value) =>
            JsonSerializer.Serialize(properties.ToDictionary(
                p => p.GetColumnName(),
                p => value(p) is { } v && p.GetTypeMapping().Converter is { } converter ? converter.ConvertToProvider(v) : value(p)));
    }

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
