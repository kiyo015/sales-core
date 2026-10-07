using Microsoft.EntityFrameworkCore;
using Npgsql;
using SalesCore.Domain;

namespace SalesCore.Integration.Tests;

/// <summary>
/// 表の対応付け(docs/domain/er-diagram.md、段階1の分)。保存して読み直しても、業務の状態と金額が変わらないこと。
/// 読み直しは ChangeTracker.Clear() の後に行う(メモリ上のオブジェクトではなく、DB から組み立て直したものを見る)。
/// </summary>
public class PersistenceMappingTests(DatabaseFixture fixture) : DatabaseTest(fixture)
{
    private static readonly DateOnly Today = new(2026, 10, 7);

    [Fact]
    public async Task マスタを保存して読み直せる()
    {
        var standard = new TaxCategory("STD", "標準");
        var rate = new TaxRatePeriod(standard, TaxRate.Of(10m), new DateOnly(2019, 10, 1));
        var product = new Product("P001", "ボルト", "箱", standard, 1234.56m);
        var warehouse = new Warehouse("W01", "本社倉庫");
        var headOffice = new Customer("C001", "本社", 20);
        var branch = new Customer("C002", "大阪支店", 31, billingCustomer: headOffice);
        Db.AddRange(rate, product, warehouse, headOffice, branch);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();

        var loadedBranch = await Db.Customers.Include(c => c.BillingCustomer).SingleAsync(c => c.Code == "C002");
        var loadedHead = await Db.Customers.SingleAsync(c => c.Code == "C001");
        var loadedProduct = await Db.Products.Include(p => p.TaxCategory).SingleAsync(p => p.Code == "P001");
        var loadedRate = await Db.TaxRatePeriods.SingleAsync(r => r.Id == rate.Id);

        Assert.Equal((31, "大阪支店"), (loadedBranch.ClosingDay, loadedBranch.Name));
        Assert.Same(loadedHead, loadedBranch.BillingTarget);
        Assert.Same(loadedHead, loadedHead.BillingTarget);
        Assert.Equal((1234.56m, "箱", "STD"), (loadedProduct.StandardPrice, loadedProduct.Unit, loadedProduct.TaxCategory.Code));
        Assert.Equal((TaxRate.Of(10m), new DateOnly(2019, 10, 1), (DateOnly?)null), (loadedRate.Rate, loadedRate.ValidFrom, loadedRate.ValidTo));
        Assert.True((await Db.Warehouses.SingleAsync(w => w.Code == "W01")).IsActive);
    }

    // 33.33円 × 3個を 1個・1個 出荷し、1個返品する。累計差分の金額(33・34・-34円)と状態が、読み直しても変わらない
    [Fact]
    public async Task 受注から出荷と返品までを保存し_読み直しても状態と金額が同じで_続きの出荷もできる()
    {
        var (customer, product, warehouse) = await SeedMastersAsync();
        var line = new SalesOrderLine(3, 33.33m, TaxRate.Of(10m));
        var order = new SalesOrder([line]);
        order.Approve();
        Db.Add(order);
        Set(order, "Number", "SO-0001");
        Set(order, "OrderedOn", Today);
        Set(order, "CustomerId", customer.Id);
        Set(line, "ProductId", product.Id);

        var first = await ShipAsync(order, line, 1, "SH-0001", "SR-0001", customer, warehouse);
        var second = await ShipAsync(order, line, 1, "SH-0002", "SR-0002", customer, warehouse);
        var returned = order.RecordReturn(Today, [new(line, 1)]);
        Db.Add(returned);
        Set(returned, "Number", "SR-0003");
        Set(returned, "CustomerId", customer.Id);
        Set(returned, "BillingCustomerId", customer.Id);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();

        var loaded = await Db.SalesOrders.Include(o => o.Lines).SingleAsync(o => o.Id == order.Id);
        var loadedLine = Assert.Single(loaded.Lines);
        Assert.Equal(SalesOrderStatus.PartiallyShipped, loaded.Status);
        Assert.Equal((3m, 33.33m, TaxRate.Of(10m)), (loadedLine.Quantity, loadedLine.UnitPrice, loadedLine.TaxRate));
        Assert.Equal((2m, 1m), (loadedLine.ShippedQuantity, loadedLine.ReturnedQuantity));

        var records = await Db.SalesRecords.Include(r => r.Lines).Include(r => r.Shipment)
            .Where(r => r.Id == first.Id || r.Id == second.Id || r.Id == returned.Id)
            .OrderBy(r => r.Id).ToListAsync();
        Assert.Equal(new[] { Money.Of(33), Money.Of(34), Money.Of(-34) }, records.Select(r => r.Amount).ToArray());
        Assert.Equal(loadedLine.SalesAmount, records.Aggregate(Money.Zero, (total, r) => total + r.Amount)); // 手元1個 = 33円
        Assert.All(records.SelectMany(r => r.Lines), l => Assert.Same(loadedLine, l.OrderLine));
        Assert.Equal((ShipmentStatus.Confirmed, (DateOnly?)Today), (records[0].Shipment!.Status, records[0].Shipment!.ShippedOn));
        Assert.Null(records[2].Shipment);

        // 読み直した受注で、残りの1個を出荷する。手元は 1個 → 2個なので、累計差分は 33 → 67 で 34円。受注は出荷済になる
        var shipment = loaded.InstructShipment([new(loadedLine, 1)]);
        var last = shipment.Confirm(Today);
        Assert.Equal(Money.Of(34), last.Amount);
        Assert.Equal(Money.Of(67), loadedLine.SalesAmount);
        Assert.Equal(SalesOrderStatus.Shipped, loaded.Status);
    }

    // 取引データは物理削除しない。外部キーは RESTRICT で、親だけを消そうとしても DB が止める(ON DELETE CASCADE なら黙って消える)
    [Fact]
    public async Task 明細がある受注はDBから直接消そうとしても消えない()
    {
        var (customer, product, _) = await SeedMastersAsync();
        var line = new SalesOrderLine(1, 100m, TaxRate.Of(10m));
        var order = new SalesOrder([line]);
        Db.Add(order);
        Set(order, "Number", "SO-0002");
        Set(order, "OrderedOn", Today);
        Set(order, "CustomerId", customer.Id);
        Set(line, "ProductId", product.Id);
        await Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(
            () => Db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM sales_orders WHERE id = {order.Id}"));

        // 23001 は ON DELETE RESTRICT が止めたとき。何も指定しない(NO ACTION)なら 23503 になるので、RESTRICT であることまで区別できる
        Assert.Equal(PostgresErrorCodes.RestrictViolation, error.SqlState);
    }

    // 同じ日に税率が2つあると、受注明細にどちらを写すかが決まらない(2026-10-07 の SQL レビューの指摘)
    [Fact]
    public async Task 同じ税区分で期間が重なる税率は登録できない()
    {
        var standard = new TaxCategory("STD", "標準");
        Db.Add(new TaxRatePeriod(standard, TaxRate.Of(10m), new DateOnly(2019, 10, 1)));
        await Db.SaveChangesAsync();
        Db.Add(new TaxRatePeriod(standard, TaxRate.Of(12m), new DateOnly(2026, 4, 1)));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => Db.SaveChangesAsync());

        Assert.Equal(PostgresErrorCodes.ExclusionViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [Fact]
    public async Task 期間が続いていれば_税率を改定できる()
    {
        var standard = new TaxCategory("STD", "標準");
        Db.Add(new TaxRatePeriod(standard, TaxRate.Of(10m), new DateOnly(2019, 10, 1), new DateOnly(2026, 3, 31)));
        Db.Add(new TaxRatePeriod(standard, TaxRate.Of(12m), new DateOnly(2026, 4, 1)));

        await Db.SaveChangesAsync();

        Assert.Equal(2, await Db.TaxRatePeriods.CountAsync());
    }

    // ドメインを通さずに SQL で直接入れても、DB が止める
    [Fact]
    public async Task 終了日が開始日より前の税率はDBに直接入れようとしても入らない()
    {
        var standard = new TaxCategory("STD", "標準");
        Db.Add(standard);
        await Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => Db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO tax_rates (tax_category_id, rate, valid_from, valid_to) VALUES ({standard.Id}, 10, DATE '2026-04-01', DATE '2026-03-31')"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    // 一意インデックスは NULL を何件でも通す。番号が NULL を許すと、番号の無い受注をいくつでも作れる(2026-10-07 の SQL レビューで見つけた)
    [Fact]
    public async Task 番号の無い受注は保存できない()
    {
        var (customer, product, _) = await SeedMastersAsync();
        var line = new SalesOrderLine(1, 100m, TaxRate.Of(10m));
        var order = new SalesOrder([line]);
        Db.Add(order);
        Set(order, "OrderedOn", Today);
        Set(order, "CustomerId", customer.Id);
        Set(line, "ProductId", product.Id);

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => Db.SaveChangesAsync());

        Assert.Equal(PostgresErrorCodes.NotNullViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [Fact]
    public async Task 得意先コードは重複できない()
    {
        Db.Add(new Customer("C900", "一社目", 31));
        await Db.SaveChangesAsync();
        Db.Add(new Customer("C900", "二社目", 31));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => Db.SaveChangesAsync());

        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    private async Task<(Customer, Product, Warehouse)> SeedMastersAsync()
    {
        var standard = new TaxCategory("STD", "標準");
        var customer = new Customer("C001", "本社", 31);
        var product = new Product("P001", "ボルト", "箱", standard, 33.33m);
        var warehouse = new Warehouse("W01", "本社倉庫");
        Db.AddRange(customer, product, warehouse);
        await Db.SaveChangesAsync();
        return (customer, product, warehouse);
    }

    private async Task<SalesRecord> ShipAsync(
        SalesOrder order, SalesOrderLine line, decimal quantity, string shipmentNumber, string recordNumber,
        Customer customer, Warehouse warehouse)
    {
        var shipment = order.InstructShipment([new(line, quantity)]);
        Db.Add(shipment);
        Set(shipment, "Number", shipmentNumber);
        Set(shipment, "WarehouseId", warehouse.Id);
        await Db.SaveChangesAsync();

        var record = shipment.Confirm(Today);
        Db.Add(record);
        Set(record, "Number", recordNumber);
        Set(record, "CustomerId", customer.Id);
        Set(record, "BillingCustomerId", customer.BillingTarget.Id);
        await Db.SaveChangesAsync();
        return record;
    }

    // 段階1では、番号や得意先などドメインの判断に使わない列を、シャドウプロパティ(エンティティに無い列)で持つ
    private void Set(object entity, string property, object value) => Db.Entry(entity).Property(property).CurrentValue = value;
}
