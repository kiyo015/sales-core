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

    // 番号を振るシーケンス(受注・出荷・売上・請求書)。番号の形は SO-000001
    private const string SalesOrderNumbers = "sales_order_number_seq", ShipmentNumbers = "shipment_number_seq", SalesRecordNumbers = "sales_record_number_seq";
    private const string InvoiceNumbers = "invoice_number_seq", QuotationNumbers = "quotation_number_seq", ReceiptNumbers = "receipt_number_seq";

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
        // 上限は6桁。lpad は桁を超えると切り詰めて番号が重なるので、その前にシーケンスの側で止める
        foreach (var sequence in new[] { SalesOrderNumbers, ShipmentNumbers, SalesRecordNumbers, InvoiceNumbers, QuotationNumbers, ReceiptNumbers })
        {
            modelBuilder.HasSequence<long>(sequence).HasMax(999999);
        }
        Customers(modelBuilder.Entity<Customer>());
        Products(modelBuilder.Entity<Product>());
        TaxCategories(modelBuilder.Entity<TaxCategory>());
        TaxRates(modelBuilder.Entity<TaxRatePeriod>());
        CustomerPrices(modelBuilder.Entity<CustomerPrice>());
        Warehouses(modelBuilder.Entity<Warehouse>());
        Quotations(modelBuilder.Entity<Quotation>());
        QuotationLines(modelBuilder.Entity<QuotationLine>());
        SalesOrders(modelBuilder.Entity<SalesOrder>());
        SalesOrderLines(modelBuilder.Entity<SalesOrderLine>());
        Shipments(modelBuilder.Entity<Shipment>());
        ShipmentLines(modelBuilder.Entity<ShipmentLine>());
        SalesRecords(modelBuilder.Entity<SalesRecord>());
        SalesRecordLines(modelBuilder.Entity<SalesRecordLine>());
        Invoices(modelBuilder.Entity<Invoice>());
        InvoiceTaxSummaries(modelBuilder.Entity<InvoiceTaxSummary>());
        Receipts(modelBuilder.Entity<Receipt>());
        ReceiptAllocations(modelBuilder.Entity<ReceiptAllocation>());
        AuditLogs(modelBuilder.Entity<AuditLog>());
    }

    private static void Customers(EntityTypeBuilder<Customer> b)
    {
        // 回収条件(2026-11-25 本人の決定)。当月の回収は、回収日が締め日より後の時だけ(支払期限が締め日以前にならない)
        b.ToTable("customers", t => t.HasCheckConstraint("ck_customers_collection",
            "collection_months BETWEEN 0 AND 3 AND collection_day IN (5, 10, 15, 20, 25, 31) AND (collection_months > 0 OR collection_day > closing_day)"));
        // 既定値は既存の得意先(翌月末)のためだけ。EF は int の 0(当月)を「値なし」と見なして DB の既定値1で入れてしまうので、
        // 値は常に送る(ValueGeneratedNever。2026-11-25 に当月が翌月で保存されるのを MasterTests で見た)
        b.Property(c => c.CollectionMonths).HasDefaultValue(1).ValueGeneratedNever();
        b.Property(c => c.CollectionDay).HasDefaultValue(31).ValueGeneratedNever();
        b.Property(c => c.Code).HasMaxLength(20);
        b.HasIndex(c => c.Code).IsUnique();
        b.Property(c => c.Name).HasMaxLength(100);
        b.Ignore(c => c.BillingTarget);
        // 請求先が自分なら NULL(ER図の初版は「自分自身のid」だったが、作成時に自分のidがまだ無いので NULL で表す)
        b.HasOne(c => c.BillingCustomer).WithMany().HasForeignKey(c => c.BillingCustomerId);
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

    private static void CustomerPrices(EntityTypeBuilder<CustomerPrice> b)
    {
        // 有効な行の期間の重なりは、マイグレーション AddCustomerPrices の排他制約 ex_customer_prices_no_overlap で止める(税率と同じく SQL で足した)。
        // 同時に2人が同じ得意先・商品の単価を登録しても、DB が後の方を止める(Domain の確かめは、読んだ時点の単価でしか見ない)
        b.ToTable("customer_prices", t => t.HasCheckConstraint("ck_customer_prices_valid_period", "valid_to IS NULL OR valid_to >= valid_from"));
        b.HasOne(p => p.Customer).WithMany().HasForeignKey("CustomerId").IsRequired();
        b.HasOne(p => p.Product).WithMany().HasForeignKey("ProductId").IsRequired();
        b.Property(p => p.UnitPrice).HasPrecision(UnitPricePrecision, UnitPriceScale);
    }

    private static void Warehouses(EntityTypeBuilder<Warehouse> b)
    {
        b.ToTable("warehouses");
        b.Property(w => w.Code).HasMaxLength(20);
        b.HasIndex(w => w.Code).IsUnique();
        b.Property(w => w.Name).HasMaxLength(100);
    }

    private static void Quotations(EntityTypeBuilder<Quotation> b)
    {
        b.ToTable("quotations", t => t.HasCheckConstraint("ck_quotations_valid_period", "valid_until >= quoted_on"));
        Number(b, "QT", QuotationNumbers);
        b.HasOne(q => q.Customer).WithMany().HasForeignKey("CustomerId").IsRequired();
        b.Property(q => q.Status).HasConversion<string>().HasMaxLength(20);
        b.HasMany(q => q.Lines).WithOne().HasForeignKey("QuotationId").IsRequired();
    }

    private static void QuotationLines(EntityTypeBuilder<QuotationLine> b)
    {
        b.ToTable("quotation_lines");
        b.Property(l => l.Quantity).HasPrecision(QuantityPrecision, QuantityScale);
        b.Property(l => l.UnitPrice).HasPrecision(UnitPricePrecision, UnitPriceScale);
        b.HasOne(l => l.Product).WithMany().HasForeignKey("ProductId").IsRequired();
    }

    private static void SalesOrders(EntityTypeBuilder<SalesOrder> b)
    {
        b.ToTable("sales_orders");
        Number(b, "SO", SalesOrderNumbers);
        b.HasOne(o => o.Customer).WithMany().HasForeignKey("CustomerId").IsRequired();
        b.Property(o => o.Status).HasConversion<string>().HasMaxLength(20);
        b.HasMany(o => o.Lines).WithOne().HasForeignKey("SalesOrderId").IsRequired();
        // 見積から作った受注の元の見積。同じ見積から受注は1つだけ(見積から作っていない受注は NULL で、一意の判定から外れる)
        b.HasOne<Quotation>().WithMany().HasForeignKey("QuotationId").IsRequired(false);
        b.HasIndex("QuotationId").IsUnique();
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
        Number(b, "SH", ShipmentNumbers);
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
        Number(b, "SR", SalesRecordNumbers);
        // 返品の売上は出荷を持たないので NULL を許す
        b.HasOne(r => r.Shipment).WithMany().HasForeignKey("ShipmentId");
        b.HasOne(r => r.Customer).WithMany().HasForeignKey("CustomerId").IsRequired();
        b.HasOne(r => r.BillingCustomer).WithMany().HasForeignKey("BillingCustomerId").IsRequired(); // 計上した時点の請求先を写す
        b.HasMany(r => r.Lines).WithOne().HasForeignKey("SalesRecordId").IsRequired();
    }

    private static void SalesRecordLines(EntityTypeBuilder<SalesRecordLine> b)
    {
        b.ToTable("sales_record_lines");
        b.Property(l => l.Quantity).HasPrecision(QuantityPrecision, QuantityScale);
        b.Property(l => l.UnitPrice).HasPrecision(UnitPricePrecision, UnitPriceScale);
        b.HasOne(l => l.OrderLine).WithMany().HasForeignKey("SalesOrderLineId").IsRequired();
    }

    private static void Invoices(EntityTypeBuilder<Invoice> b)
    {
        // 消し込んだ額は0以上、今回分(今回売上＋消費税)以下(業務ルール集「入金と消込」。2026-11-20)
        b.ToTable("invoices", t => t.HasCheckConstraint("ck_invoices_allocated", "allocated_amount BETWEEN 0 AND sales_amount + tax_amount"));
        Number(b, "IN", InvoiceNumbers);
        b.HasOne(i => i.BillingCustomer).WithMany().HasForeignKey("BillingCustomerId").IsRequired();
        b.HasIndex("BillingCustomerId", nameof(Invoice.ClosingDate)).IsUnique(); // 同じ請求先・同じ締め日に1枚
        // 支払期限(due_date)は 2026-11-25 から締めた時に決める。それより前に締めた請求書は NULL
        b.Property(i => i.Status).HasConversion<string>().HasMaxLength(20);
        b.Ignore(i => i.Tax); // invoice_tax_summaries から組み立てる
        // 締めで請求書に入るまで、売上の invoice_id は NULL
        b.HasMany(i => i.SalesRecords).WithOne(r => r.Invoice).HasForeignKey(r => r.InvoiceId);
        b.HasMany<InvoiceTaxSummary>("_taxSummaries").WithOne().HasForeignKey("InvoiceId").IsRequired();
    }

    private static void InvoiceTaxSummaries(EntityTypeBuilder<InvoiceTaxSummary> b)
    {
        b.ToTable("invoice_tax_summaries");
    }

    private static void Receipts(EntityTypeBuilder<Receipt> b)
    {
        // 割当の合計は入金額を超えず、符号も同じ(打ち消しの入金は負)。入金額が0の行もここで止まる
        b.ToTable("receipts", t => t.HasCheckConstraint("ck_receipts_allocated",
            "(amount > 0 AND allocated_amount BETWEEN 0 AND amount) OR (amount < 0 AND allocated_amount BETWEEN amount AND 0)"));
        Number(b, "RC", ReceiptNumbers);
        b.HasOne(r => r.BillingCustomer).WithMany().HasForeignKey("BillingCustomerId").IsRequired();
        // 打ち消しの入金は元の入金を1つだけ指す(一意インデックス。同時に2人が取り消しても、元の入金の版数とここで止まる)
        b.HasOne(r => r.Reverses).WithOne().HasForeignKey<Receipt>(r => r.ReversesId);
        b.HasMany(r => r.Allocations).WithOne().HasForeignKey("ReceiptId").IsRequired();
    }

    private static void ReceiptAllocations(EntityTypeBuilder<ReceiptAllocation> b)
    {
        b.ToTable("receipt_allocations");
        b.HasOne(a => a.Invoice).WithMany().HasForeignKey("InvoiceId").IsRequired();
    }

    // 番号は必須で重複しない。値を入れずに保存すると DB がシーケンスで振る(欠番あり。2026-10-15 決定)。
    // 必須にするのは、文字列のシャドウプロパティは既定で NULL を許し、一意インデックスは NULL を何件でも通すため
    // (2026-10-07 の SQL レビューで、番号の無い受注を作れる状態を見つけた)
    private static void Number(EntityTypeBuilder b, string prefix, string sequence)
    {
        b.Property<string>("Number").HasMaxLength(20).IsRequired()
            .HasDefaultValueSql($"'{prefix}-' || lpad(nextval('{sequence}')::text, 6, '0')");
        b.HasIndex("Number").IsUnique();
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
