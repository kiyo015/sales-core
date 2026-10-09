using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesCore.Domain;

namespace SalesCore.Infrastructure.Persistence;

/// <summary>
/// 表ごとの対応付け。列名は <see cref="SalesCoreDbContext"/> が snake_case にそろえ、外部キーはすべて RESTRICT にする。
/// ドメインの判断に使わない列(番号・日付・得意先・商品・倉庫)は、段階1ではシャドウプロパティ(エンティティに無い列)で持つ。
/// ドメインが使うようになったら、エンティティのプロパティに移す(列は変わらないので、マイグレーションは要らない)。
/// </summary>
internal static class Configurations
{
    // 金額・数量の桁。業務ルール集「1. 金額と数量」
    private const int UnitPricePrecision = 15, UnitPriceScale = 2;
    private const int QuantityPrecision = 15, QuantityScale = 3;

    // 全表に共通する列(監査ログの表を除く)。値は保存のときに SalesCoreDbContext が入れる
    public const string CreatedAt = "CreatedAt", CreatedBy = "CreatedBy", UpdatedAt = "UpdatedAt", UpdatedBy = "UpdatedBy";
    public const string RowVersion = "RowVersion";
    public static readonly IReadOnlySet<string> CommonColumns = new HashSet<string> { CreatedAt, CreatedBy, UpdatedAt, UpdatedBy, RowVersion };

    public static void Apply(ModelBuilder modelBuilder)
    {
        ApplyTables(modelBuilder);

        foreach (var entity in modelBuilder.Model.GetEntityTypes().Where(e => e.ClrType != typeof(AuditLog)).ToList())
        {
            var b = modelBuilder.Entity(entity.ClrType);
            // 既存の行には、マイグレーションを当てた時点の日時が入る(now())。新しい行には保存のときの日時を入れる
            b.Property<DateTimeOffset>(CreatedAt).HasDefaultValueSql("now()");
            b.Property<string>(CreatedBy).HasMaxLength(100); // ユーザーを作る段階2までは NULL
            b.Property<DateTimeOffset>(UpdatedAt).HasDefaultValueSql("now()");
            b.Property<string>(UpdatedBy).HasMaxLength(100);
            // 楽観ロックの版数。更新のたびに1上げ、保存のときに読んだ時点の値と比べる(違えば DbUpdateConcurrencyException)。
            // PostgreSQL の xmin は同じトランザクションの中では変わらず、トランザクションを取り消す結合テストで衝突を再現できないので使わない
            b.Property<int>(RowVersion).IsConcurrencyToken();
        }
    }

    private static void ApplyTables(ModelBuilder modelBuilder)
    {
        Customers(modelBuilder.Entity<Customer>());
        Products(modelBuilder.Entity<Product>());
        TaxCategories(modelBuilder.Entity<TaxCategory>());
        TaxRates(modelBuilder.Entity<TaxRatePeriod>());
        Warehouses(modelBuilder.Entity<Warehouse>());
        SalesOrders(modelBuilder.Entity<SalesOrder>());
        SalesOrderLines(modelBuilder.Entity<SalesOrderLine>());
        Shipments(modelBuilder.Entity<Shipment>());
        ShipmentLines(modelBuilder.Entity<ShipmentLine>());
        SalesRecords(modelBuilder.Entity<SalesRecord>());
        SalesRecordLines(modelBuilder.Entity<SalesRecordLine>());
        AuditLogs(modelBuilder.Entity<AuditLog>());
    }

    private static void Customers(EntityTypeBuilder<Customer> b)
    {
        b.ToTable("customers");
        b.Property(c => c.Code).HasMaxLength(20);
        b.HasIndex(c => c.Code).IsUnique();
        b.Property(c => c.Name).HasMaxLength(100);
        b.Ignore(c => c.BillingTarget);
        // 請求先が自分なら NULL(ER図の初版は「自分自身のid」だったが、作成時に自分のidがまだ無いので NULL で表す)
        b.HasOne(c => c.BillingCustomer).WithMany().HasForeignKey("BillingCustomerId");
    }

    private static void Products(EntityTypeBuilder<Product> b)
    {
        b.ToTable("products");
        b.Property(p => p.Code).HasMaxLength(20);
        b.HasIndex(p => p.Code).IsUnique();
        b.Property(p => p.Name).HasMaxLength(100);
        b.Property(p => p.Unit).HasMaxLength(10);
        b.Property(p => p.StandardPrice).HasPrecision(UnitPricePrecision, UnitPriceScale);
        b.HasOne(p => p.TaxCategory).WithMany().HasForeignKey("TaxCategoryId").IsRequired();
    }

    private static void TaxCategories(EntityTypeBuilder<TaxCategory> b)
    {
        b.ToTable("tax_categories");
        b.Property(t => t.Code).HasMaxLength(20);
        b.HasIndex(t => t.Code).IsUnique();
        b.Property(t => t.Name).HasMaxLength(50);
    }

    private static void TaxRates(EntityTypeBuilder<TaxRatePeriod> b)
    {
        // 期間の重なり(同じ日に税率が2つ)は、マイグレーション AddStageOneTables の排他制約 ex_tax_rates_no_overlap で止める。
        // EF Core に排他制約を書く API が無いので、マイグレーションに SQL で足している(2026-10-07 の SQL レビューの指摘)
        b.ToTable("tax_rates", t => t.HasCheckConstraint("ck_tax_rates_valid_period", "valid_to IS NULL OR valid_to >= valid_from"));
        b.HasOne(t => t.TaxCategory).WithMany().HasForeignKey("TaxCategoryId").IsRequired();
        b.HasIndex("TaxCategoryId", nameof(TaxRatePeriod.ValidFrom)).IsUnique(); // 同じ税区分・同じ開始日の税率は1つ
    }

    private static void Warehouses(EntityTypeBuilder<Warehouse> b)
    {
        b.ToTable("warehouses");
        b.Property(w => w.Code).HasMaxLength(20);
        b.HasIndex(w => w.Code).IsUnique();
        b.Property(w => w.Name).HasMaxLength(100);
    }

    private static void SalesOrders(EntityTypeBuilder<SalesOrder> b)
    {
        b.ToTable("sales_orders");
        // 番号は必須。文字列のシャドウプロパティは既定で NULL を許し、一意インデックスは NULL を何件でも通すため
        // (2026-10-07 の SQL レビューで、番号の無い受注を作れる状態を見つけた)
        b.Property<string>("Number").HasMaxLength(20).IsRequired();
        b.HasIndex("Number").IsUnique();
        b.Property<DateOnly>("OrderedOn");
        b.HasOne<Customer>().WithMany().HasForeignKey("CustomerId").IsRequired();
        b.Property(o => o.Status).HasConversion<string>().HasMaxLength(20);
        b.HasMany(o => o.Lines).WithOne().HasForeignKey("SalesOrderId").IsRequired();
    }

    private static void SalesOrderLines(EntityTypeBuilder<SalesOrderLine> b)
    {
        b.ToTable("sales_order_lines");
        b.Property(l => l.Quantity).HasPrecision(QuantityPrecision, QuantityScale);
        b.Property(l => l.UnitPrice).HasPrecision(UnitPricePrecision, UnitPriceScale);
        b.Property(l => l.ShippedQuantity).HasPrecision(QuantityPrecision, QuantityScale);
        b.Property(l => l.ReturnedQuantity).HasPrecision(QuantityPrecision, QuantityScale);
        b.HasOne<Product>().WithMany().HasForeignKey("ProductId").IsRequired();
    }

    private static void Shipments(EntityTypeBuilder<Shipment> b)
    {
        b.ToTable("shipments");
        b.Property<string>("Number").HasMaxLength(20).IsRequired();
        b.HasIndex("Number").IsUnique();
        b.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
        b.HasOne<SalesOrder>("_order").WithMany().HasForeignKey("SalesOrderId").IsRequired();
        b.HasOne<Warehouse>().WithMany().HasForeignKey("WarehouseId").IsRequired();
        b.HasMany(s => s.Lines).WithOne().HasForeignKey("ShipmentId").IsRequired();
    }

    private static void ShipmentLines(EntityTypeBuilder<ShipmentLine> b)
    {
        b.ToTable("shipment_lines");
        b.Property(l => l.Quantity).HasPrecision(QuantityPrecision, QuantityScale);
        b.HasOne(l => l.OrderLine).WithMany().HasForeignKey("SalesOrderLineId").IsRequired();
        b.HasIndex("ShipmentId", "SalesOrderLineId").IsUnique(); // 1回の出荷に同じ受注明細は1行(ドメインでも止めている)
    }

    private static void SalesRecords(EntityTypeBuilder<SalesRecord> b)
    {
        b.ToTable("sales_records");
        b.Property<string>("Number").HasMaxLength(20).IsRequired();
        b.HasIndex("Number").IsUnique();
        // 返品の売上は出荷を持たないので NULL を許す
        b.HasOne(r => r.Shipment).WithMany().HasForeignKey("ShipmentId");
        b.HasOne<Customer>().WithMany().HasForeignKey("CustomerId").IsRequired();
        b.HasOne<Customer>().WithMany().HasForeignKey("BillingCustomerId").IsRequired(); // 計上した時点の請求先を写す
        b.HasMany(r => r.Lines).WithOne().HasForeignKey("SalesRecordId").IsRequired();
    }

    private static void SalesRecordLines(EntityTypeBuilder<SalesRecordLine> b)
    {
        b.ToTable("sales_record_lines");
        b.Property(l => l.Quantity).HasPrecision(QuantityPrecision, QuantityScale);
        b.Property(l => l.UnitPrice).HasPrecision(UnitPricePrecision, UnitPriceScale);
        b.HasOne(l => l.OrderLine).WithMany().HasForeignKey("SalesOrderLineId").IsRequired();
    }

    private static void AuditLogs(EntityTypeBuilder<AuditLog> b)
    {
        b.ToTable("audit_logs");
        b.Property(a => a.UserId).HasMaxLength(100);
        b.Property(a => a.EntityType).HasMaxLength(100);
        b.Property(a => a.Action).HasMaxLength(20);
        b.Property(a => a.BeforeValues).HasColumnType("jsonb");
        b.Property(a => a.AfterValues).HasColumnType("jsonb");
        b.HasIndex(a => new { a.EntityType, a.EntityId });
    }
}
