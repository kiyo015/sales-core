using SalesCore.Domain;
using SalesCore.Infrastructure.Persistence;

namespace SalesCore.Integration.Tests;

/// <summary>
/// 結合テストでデータを組み立てる役。番号・日付・得意先などのシャドウプロパティ(エンティティに無い列)を、ここでまとめて設定する。
/// 段階1ではこれらを決めるアプリケーション層がまだ無い(受注・出荷の API は Day30)ので、テストが代わりに決める。
/// 番号はこのシナリオの中で連番にする。テストはトランザクションごと取り消すので、別のテストと重ならない。
/// 同じテストの中で2つ目のセッション(<see cref="DatabaseTest.CreateAnotherSession"/>)を使うときは、
/// 番号が重ならないように接頭辞を変える。
/// </summary>
internal sealed class TestScenario(SalesCoreDbContext db, string numberPrefix = "")
{
    private int _sequence;

    public sealed record MasterData(
        TaxCategory Standard, TaxCategory Reduced, Product StandardProduct, Product ReducedProduct,
        Customer HeadOffice, Customer Branch, Customer OtherCustomer, Warehouse Warehouse);

    /// <summary>
    /// 標準税率(10%)と軽減税率(8%)の商品を1つずつ、本社・支店(請求先は本社)・別の得意先、倉庫1件。
    /// 単価は、明細ごとに丸めると税額がずれる 105円と、累計差分で端数が出る 33.33円。
    /// </summary>
    public async Task<MasterData> SeedMastersAsync()
    {
        var standard = new TaxCategory("STD", "標準");
        var reduced = new TaxCategory("RED", "軽減");
        var headOffice = new Customer("C001", "本社", 31);
        var masters = new MasterData(
            standard,
            reduced,
            new Product("P001", "ボルト", "箱", standard, 105m),
            new Product("P002", "お茶", "本", reduced, 33.33m),
            headOffice,
            new Customer("C002", "大阪支店", 31, billingCustomer: headOffice),
            new Customer("C003", "別の会社", 31),
            new Warehouse("W01", "本社倉庫"));
        db.AddRange(
            new TaxRatePeriod(standard, TaxRate.Of(10m), new DateOnly(2019, 10, 1)),
            new TaxRatePeriod(reduced, TaxRate.Of(8m), new DateOnly(2019, 10, 1)),
            masters.StandardProduct, masters.ReducedProduct, masters.Branch, masters.OtherCustomer, masters.Warehouse);
        await db.SaveChangesAsync();
        return masters;
    }

    /// <summary>受注を登録する。明細の単価と税率は商品から写す(受注時点の値)。</summary>
    public SalesOrder AddOrder(Customer customer, DateOnly orderedOn, params (Product Product, decimal Quantity, TaxRate Rate)[] lines)
    {
        var orderLines = lines.Select(l => (Line: new SalesOrderLine(l.Quantity, l.Product.StandardPrice, l.Rate), l.Product)).ToList();
        var order = new SalesOrder(orderLines.Select(l => l.Line));
        db.Add(order);
        Set(order, "Number", NextNumber("SO"));
        Set(order, "OrderedOn", orderedOn);
        Set(order, "CustomerId", customer.Id);
        foreach (var (line, product) in orderLines)
        {
            Set(line, "ProductId", product.Id);
        }
        return order;
    }

    public Shipment AddShipment(Shipment shipment, Warehouse warehouse)
    {
        db.Add(shipment);
        Set(shipment, "Number", NextNumber("SH"));
        Set(shipment, "WarehouseId", warehouse.Id);
        return shipment;
    }

    /// <summary>売上を登録する。請求先は計上した時点の請求先を写す(支店の売上なら本社)。</summary>
    public SalesRecord AddSalesRecord(SalesRecord record, Customer customer)
    {
        db.Add(record);
        Set(record, "Number", NextNumber("SR"));
        Set(record, "CustomerId", customer.Id);
        Set(record, "BillingCustomerId", customer.BillingTarget.Id);
        return record;
    }

    /// <summary>
    /// 1つの操作(画面や API の1回の呼び出し)の終わり。保存して、メモリ上のオブジェクトを手放す。
    /// 次の操作では DB から読み直したものを使うので、保存し忘れや対応付けの漏れが結果に出る。
    /// </summary>
    public async Task EndRequestAsync()
    {
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private string NextNumber(string prefix) => $"{numberPrefix}{prefix}-{++_sequence:D4}";

    private void Set(object entity, string property, object value) => db.Entry(entity).Property(property).CurrentValue = value;
}
