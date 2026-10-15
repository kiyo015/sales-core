using Microsoft.EntityFrameworkCore;
using SalesCore.Domain;
using SalesCore.Infrastructure.Persistence;

namespace SalesCore.Application;

/// <summary>
/// 受注・出荷のユースケース。1回の呼び出しが1回の操作で、DB から読み直したものに Domain の操作をして保存する。
/// 業務の判断(状態遷移・数量・金額)は Domain が行い、ここではマスタを引き、エンティティに無い列
/// (番号以外のシャドウプロパティ: 得意先・商品・倉庫・日付)を埋める。番号は DB が振る。
/// </summary>
public sealed class SalesOrderUseCases(SalesCoreDbContext db)
{
    /// <summary>受注を登録する。単価を省くと商品の標準単価、税率は受注日時点の商品の税区分の税率。</summary>
    public async Task<SalesOrderView> CreateAsync(CreateSalesOrder request)
    {
        var customer = await db.Customers.SingleOrDefaultAsync(c => c.Code == request.CustomerCode && c.IsActive)
            ?? throw new ArgumentException($"得意先コード {request.CustomerCode} の得意先が無い(無効を含む)。");

        var lines = new List<(SalesOrderLine Line, Product Product)>();
        foreach (var requested in request.Lines ?? [])
        {
            var product = await db.Products.Include(p => p.TaxCategory).SingleOrDefaultAsync(p => p.Code == requested.ProductCode && p.IsActive)
                ?? throw new ArgumentException($"商品コード {requested.ProductCode} の商品が無い(無効を含む)。");
            var rate = await TaxRateOnAsync(product, request.OrderedOn);
            lines.Add((new SalesOrderLine(requested.Quantity, requested.UnitPrice ?? product.StandardPrice, rate), product));
        }

        var order = new SalesOrder(lines.Select(l => l.Line));
        db.Add(order);
        db.Entry(order).Property("OrderedOn").CurrentValue = request.OrderedOn;
        db.Entry(order).Property("CustomerId").CurrentValue = customer.Id;
        foreach (var (line, product) in lines)
        {
            db.Entry(line).Property("ProductId").CurrentValue = product.Id;
        }
        await db.SaveChangesAsync();
        return await GetAsync(order.Id);
    }

    public async Task<SalesOrderView> GetAsync(long id)
    {
        var order = await db.SalesOrders.AsNoTracking().Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == id)
            ?? throw new NotFoundException($"受注 {id} が無い。");
        var header = await (
                from o in db.SalesOrders
                join c in db.Customers on EF.Property<long>(o, "CustomerId") equals c.Id
                where o.Id == id
                select new { Number = EF.Property<string>(o, "Number"), OrderedOn = EF.Property<DateOnly>(o, "OrderedOn"), CustomerCode = c.Code })
            .SingleAsync();
        var lineIds = order.Lines.Select(l => l.Id).ToList();
        var productCodes = await (
                from l in db.Set<SalesOrderLine>()
                join p in db.Products on EF.Property<long>(l, "ProductId") equals p.Id
                where lineIds.Contains(l.Id)
                select new { l.Id, p.Code })
            .ToDictionaryAsync(l => l.Id, l => l.Code);

        return new SalesOrderView(order.Id, header.Number, order.Status.ToString(), header.CustomerCode, header.OrderedOn, order.Amount.Yen,
            order.Lines.OrderBy(l => l.Id)
                .Select(l => new SalesOrderLineView(l.Id, productCodes[l.Id], l.Quantity, l.UnitPrice, l.TaxRate.Percent, l.ShippedQuantity, l.RemainingQuantity))
                .ToList());
    }

    public Task<SalesOrderView> ApproveAsync(long id) => ChangeOrderAsync(id, order => order.Approve());

    public Task<SalesOrderView> CancelAsync(long id) => ChangeOrderAsync(id, order => order.Cancel());

    /// <summary>出荷を指示する。受注の状態と出荷済み数量は、確定するまで変わらない。</summary>
    public async Task<ShipmentView> InstructShipmentAsync(long orderId, InstructShipment request)
    {
        var order = await LoadOrderAsync(orderId);
        var warehouse = await db.Warehouses.SingleOrDefaultAsync(w => w.Code == request.WarehouseCode && w.IsActive)
            ?? throw new ArgumentException($"倉庫コード {request.WarehouseCode} の倉庫が無い(無効を含む)。");
        var lines = (request.Lines ?? []).Select(l => new OrderLineQuantity(
            order.Lines.SingleOrDefault(line => line.Id == l.OrderLineId)
                ?? throw new ArgumentException($"受注明細 {l.OrderLineId} はこの受注の明細ではない。"),
            l.Quantity));

        var shipment = order.InstructShipment(lines);
        db.Add(shipment);
        db.Entry(shipment).Property("WarehouseId").CurrentValue = warehouse.Id;
        await db.SaveChangesAsync();
        return await GetShipmentAsync(shipment.Id);
    }

    public async Task<ShipmentView> GetShipmentAsync(long id)
    {
        var shipment = await db.Shipments.AsNoTracking().Include(s => s.Lines).ThenInclude(l => l.OrderLine).SingleOrDefaultAsync(s => s.Id == id)
            ?? throw new NotFoundException($"出荷 {id} が無い。");
        var header = await db.Shipments.Where(s => s.Id == id)
            .Select(s => new { Number = EF.Property<string>(s, "Number"), OrderId = EF.Property<long>(s, "SalesOrderId") })
            .SingleAsync();
        var record = await db.SalesRecords.Where(r => r.Shipment != null && r.Shipment.Id == id)
            .Select(r => new SalesRecordView(r.Id, EF.Property<string>(r, "Number"), r.RecordedOn, 0m))
            .SingleOrDefaultAsync();
        if (record is not null)
        {
            // 金額は明細から Money で足す(DB の SUM に任せない)
            var amount = (await db.SalesRecords.AsNoTracking().Include(r => r.Lines).SingleAsync(r => r.Id == record.Id)).Amount;
            record = record with { Amount = amount.Yen };
        }

        return new ShipmentView(shipment.Id, header.Number, shipment.Status.ToString(), header.OrderId, shipment.ShippedOn,
            shipment.Lines.OrderBy(l => l.Id).Select(l => new ShipmentLineRequest(l.OrderLine.Id, l.Quantity)).ToList(), record);
    }

    /// <summary>出荷を確定し、売上を計上する。売上の請求先は、確定した時点の得意先の請求先を写す。</summary>
    public async Task<ShipmentView> ConfirmShipmentAsync(long id, ConfirmShipment request)
    {
        var shipment = await db.Shipments.Include("_order.Lines").Include(s => s.Lines).ThenInclude(l => l.OrderLine).SingleOrDefaultAsync(s => s.Id == id)
            ?? throw new NotFoundException($"出荷 {id} が無い。");
        var orderId = (long)db.Entry(shipment).Property("SalesOrderId").CurrentValue!;
        var customerId = await db.SalesOrders.Where(o => o.Id == orderId).Select(o => EF.Property<long>(o, "CustomerId")).SingleAsync();
        var customer = await db.Customers.Include(c => c.BillingCustomer).SingleAsync(c => c.Id == customerId);

        var record = shipment.Confirm(request.ShippedOn);
        db.Add(record);
        db.Entry(record).Property("CustomerId").CurrentValue = customer.Id;
        db.Entry(record).Property("BillingCustomerId").CurrentValue = customer.BillingTarget.Id;
        await db.SaveChangesAsync();
        return await GetShipmentAsync(id);
    }

    public async Task<ShipmentView> CancelShipmentAsync(long id)
    {
        var shipment = await db.Shipments.SingleOrDefaultAsync(s => s.Id == id)
            ?? throw new NotFoundException($"出荷 {id} が無い。");
        shipment.Cancel();
        await db.SaveChangesAsync();
        return await GetShipmentAsync(id);
    }

    private async Task<SalesOrderView> ChangeOrderAsync(long id, Action<SalesOrder> change)
    {
        change(await LoadOrderAsync(id));
        await db.SaveChangesAsync();
        return await GetAsync(id);
    }

    private async Task<SalesOrder> LoadOrderAsync(long id) =>
        await db.SalesOrders.Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == id)
            ?? throw new NotFoundException($"受注 {id} が無い。");

    // 税率は税区分と期間で決まる(業務ルール集「2. 消費税」)。期間が重ならないことは DB の排他制約が保証する
    // 期間の行ごと読む(税率の値だけを読むと、0%(非課税)と「無い」の区別が付かない)
    private async Task<TaxRate> TaxRateOnAsync(Product product, DateOnly date) =>
        (await db.TaxRatePeriods
            .Where(r => r.TaxCategory.Id == product.TaxCategory.Id && r.ValidFrom <= date && (r.ValidTo == null || r.ValidTo >= date))
            .SingleOrDefaultAsync())?.Rate
            ?? throw new ArgumentException($"商品 {product.Code} の税区分に、{date:yyyy-MM-dd} 時点の税率が無い。");
}
