using Microsoft.EntityFrameworkCore;
using Npgsql;
using SalesCore.Domain;

namespace SalesCore.Integration.Tests;

/// <summary>
/// 表の対応付け(docs/domain/er-diagram.md、段階1の分)。保存して読み直しても、業務の状態と金額が変わらないこと。
/// 読み直しは ChangeTracker.Clear() の後に行う(メモリ上のオブジェクトではなく、DB から組み立て直したものを見る)。
/// 数を数えるときは、このテストが作った行に絞る(手元のテスト用DBに別の行があっても、CI のまっさらなDBと同じ結果になるように)。
/// </summary>
public class PersistenceMappingTests(DatabaseFixture fixture) : DatabaseTest(fixture)
{
    private static readonly DateOnly Today = new(2026, 10, 7);
    private static readonly TaxRate Reduced = TaxRate.Of(8m);

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

        var loadedBranch = await Db.Customers.Include(c => c.BillingCustomer).SingleAsync(c => c.Id == branch.Id);
        var loadedHead = await Db.Customers.SingleAsync(c => c.Id == headOffice.Id);
        var loadedProduct = await Db.Products.Include(p => p.TaxCategory).SingleAsync(p => p.Id == product.Id);
        var loadedRate = await Db.TaxRatePeriods.SingleAsync(r => r.Id == rate.Id);

        Assert.Equal((31, "大阪支店"), (loadedBranch.ClosingDay, loadedBranch.Name));
        Assert.Same(loadedHead, loadedBranch.BillingTarget);
        Assert.Same(loadedHead, loadedHead.BillingTarget);
        Assert.Equal((1234.56m, "箱", "STD"), (loadedProduct.StandardPrice, loadedProduct.Unit, loadedProduct.TaxCategory.Code));
        Assert.Equal((TaxRate.Of(10m), new DateOnly(2019, 10, 1), (DateOnly?)null), (loadedRate.Rate, loadedRate.ValidFrom, loadedRate.ValidTo));
        Assert.True((await Db.Warehouses.SingleAsync(w => w.Id == warehouse.Id)).IsActive);
    }

    // 33.33円 × 3本を 1本・1本 出荷し、1本返品する。累計差分の金額(33・34・-34円)と状態が、読み直しても変わらない
    [Fact]
    public async Task 受注から出荷と返品までを保存し_読み直しても状態と金額が同じで_続きの出荷もできる()
    {
        var scenario = new TestScenario(Db);
        var m = await scenario.SeedMastersAsync();
        var order = scenario.AddOrder(m.HeadOffice, Today, (m.ReducedProduct, 3, Reduced));
        order.Approve();
        var line = order.Lines[0];

        var first = await ShipAsync(scenario, order, line, m);
        var second = await ShipAsync(scenario, order, line, m);
        var returned = scenario.AddSalesRecord(order.RecordReturn(Today, [new(line, 1)]), m.HeadOffice);
        await scenario.EndRequestAsync();

        var loaded = await Db.SalesOrders.Include(o => o.Lines).SingleAsync(o => o.Id == order.Id);
        var loadedLine = Assert.Single(loaded.Lines);
        Assert.Equal(SalesOrderStatus.PartiallyShipped, loaded.Status);
        Assert.Equal((3m, 33.33m, Reduced), (loadedLine.Quantity, loadedLine.UnitPrice, loadedLine.TaxRate));
        Assert.Equal((2m, 1m), (loadedLine.ShippedQuantity, loadedLine.ReturnedQuantity));

        var records = await Db.SalesRecords.Include(r => r.Lines).Include(r => r.Shipment)
            .Where(r => r.Id == first.Id || r.Id == second.Id || r.Id == returned.Id)
            .OrderBy(r => r.Id).ToListAsync();
        Assert.Equal(new[] { Money.Of(33), Money.Of(34), Money.Of(-34) }, records.Select(r => r.Amount).ToArray());
        Assert.Equal(loadedLine.SalesAmount, records.Aggregate(Money.Zero, (total, r) => total + r.Amount)); // 手元1本 = 33円
        Assert.All(records.SelectMany(r => r.Lines), l => Assert.Same(loadedLine, l.OrderLine));
        Assert.Equal((ShipmentStatus.Confirmed, (DateOnly?)Today), (records[0].Shipment!.Status, records[0].Shipment!.ShippedOn));
        Assert.Null(records[2].Shipment);

        // 読み直した受注で、残りの1本を出荷する。手元は 1本 → 2本なので、累計差分は 33 → 67 で 34円。受注は出荷済になる
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
        var scenario = new TestScenario(Db);
        var m = await scenario.SeedMastersAsync();
        var order = scenario.AddOrder(m.HeadOffice, Today, (m.StandardProduct, 1, TaxRate.Of(10m)));
        await scenario.EndRequestAsync();

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

        Assert.Equal(2, await Db.TaxRatePeriods.CountAsync(r => r.TaxCategory.Id == standard.Id));
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
        var m = await new TestScenario(Db).SeedMastersAsync();
        var line = new SalesOrderLine(1, 100m, TaxRate.Of(10m));
        var order = new SalesOrder([line]);
        Db.Add(order);
        Db.Entry(order).Property("OrderedOn").CurrentValue = Today;
        Db.Entry(order).Property("CustomerId").CurrentValue = m.HeadOffice.Id;
        Db.Entry(line).Property("ProductId").CurrentValue = m.StandardProduct.Id;

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

    // 指示と確定を1回ずつ保存する。読み直しは最後にまとめて確かめるので、ここではメモリ上のオブジェクトのまま続ける
    private async Task<SalesRecord> ShipAsync(TestScenario scenario, SalesOrder order, SalesOrderLine line, TestScenario.MasterData m)
    {
        var shipment = scenario.AddShipment(order.InstructShipment([new(line, 1)]), m.Warehouse);
        await Db.SaveChangesAsync();
        var record = scenario.AddSalesRecord(shipment.Confirm(Today), m.HeadOffice);
        await Db.SaveChangesAsync();
        return record;
    }
}
