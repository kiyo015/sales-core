using Microsoft.EntityFrameworkCore;
using SalesCore.Domain;

namespace SalesCore.Integration.Tests;

/// <summary>
/// 受注 → 出荷(分納)→ 返品 → 請求を、DB を通して1本で流す。
/// 操作ごとに保存して読み直す(<see cref="TestScenario.EndRequestAsync"/>)ので、各操作は DB から組み立て直した状態で動く。
/// 請求書の表は Day31 で作るので、請求書は「締め期間の売上を DB から集めて作る」ところまでを確かめ、保存はしない。
/// 期待値は業務ルール集の計算を手で行ったもの(下のコメント)。製品コードの式を使って出していない。
/// </summary>
public class SalesFlowTests(DatabaseFixture fixture) : DatabaseTest(fixture)
{
    private static readonly TaxRate Standard = TaxRate.Of(10m);
    private static readonly TaxRate Reduced = TaxRate.Of(8m);

    // 支店(請求先は本社)が、ボルト(105円・10%)3箱とお茶(33.33円・8%)3本を受注。10月に3回に分けて出荷し、お茶を1本返品。
    //   出荷1(10/5) : ボルト1・お茶2 → 105円、お茶 Round(66.66)=67 − 0 = 67円
    //   出荷2(10/12): ボルト1        → 105円
    //   出荷3(10/19): ボルト1・お茶1 → 105円、お茶 Round(99.99)=100 − 67 = 33円
    //   返品(10/20) : お茶1          → お茶 Round(66.66)=67 − 100 = −33円
    // 10月末締めの本社あての請求書:
    //   10%: 対象額 105×3 = 315円、税額 31.5 → 32円(売上ごとに丸めると 10.5→11 が3回で 33円になる)
    //   8% : 対象額 67+33−33 = 67円、税額 5.36 → 5円
    //   合計: 税抜 382円 + 消費税 37円 = 419円
    [Fact]
    public async Task 受注から分納と返品を経て_締め期間の売上で請求書を作ると税率ごとに1回だけ丸めた額になる()
    {
        var scenario = new TestScenario(Db);
        var m = await scenario.SeedMastersAsync();

        // 受注・承認
        var order = scenario.AddOrder(m.Branch, new DateOnly(2026, 10, 1), (m.StandardProduct, 3, Standard), (m.ReducedProduct, 3, Reduced));
        order.Approve();
        await scenario.EndRequestAsync();

        await ShipAsync(scenario, order.Id, m, new DateOnly(2026, 10, 5), bolts: 1, tea: 2);
        await ShipAsync(scenario, order.Id, m, new DateOnly(2026, 10, 12), bolts: 1, tea: 0);
        await ShipAsync(scenario, order.Id, m, new DateOnly(2026, 10, 19), bolts: 1, tea: 1);

        // 返品
        var returning = await LoadOrderAsync(order.Id);
        var teaLine = returning.Lines.Single(l => l.TaxRate == Reduced);
        scenario.AddSalesRecord(returning.RecordReturn(new DateOnly(2026, 10, 20), [new(teaLine, 1)]), m.Branch);
        await scenario.EndRequestAsync();

        // 請求の対象にならない売上: 11月の売上(締め期間の外)と、別の請求先の売上
        var other = scenario.AddOrder(m.OtherCustomer, new DateOnly(2026, 10, 1), (m.StandardProduct, 1, Standard));
        other.Approve();
        var otherShipment = scenario.AddShipment(other.InstructShipment([new(other.Lines[0], 1)]), m.Warehouse);
        scenario.AddSalesRecord(otherShipment.Confirm(new DateOnly(2026, 10, 15)), m.OtherCustomer);
        var later = scenario.AddOrder(m.Branch, new DateOnly(2026, 10, 25), (m.StandardProduct, 1, Standard));
        later.Approve();
        var laterShipment = scenario.AddShipment(later.InstructShipment([new(later.Lines[0], 1)]), m.Warehouse);
        scenario.AddSalesRecord(laterShipment.Confirm(new DateOnly(2026, 11, 2)), m.Branch);
        await scenario.EndRequestAsync();

        // 10月末締め: 本社あての 10/1〜10/31 の売上を集めて請求書を作る
        var records = await Db.SalesRecords
            .Include(r => r.Lines)
            .Where(r => EF.Property<long>(r, "BillingCustomerId") == m.HeadOffice.Id)
            .Where(r => r.RecordedOn >= new DateOnly(2026, 10, 1) && r.RecordedOn <= new DateOnly(2026, 10, 31))
            .ToListAsync();
        var invoice = Invoice.Create(records);

        Assert.Equal(4, records.Count); // 出荷3回 + 返品1回。11月の売上と、別の請求先の売上は入らない
        Assert.Collection(
            invoice.Tax.Summaries,
            s => Assert.Equal(new TaxSummary(Standard, Money.Of(315), Money.Of(32)), s),
            s => Assert.Equal(new TaxSummary(Reduced, Money.Of(67), Money.Of(5)), s));
        Assert.Equal(Money.Of(419), invoice.Tax.TotalWithTax);

        // 受注は出荷済になり、明細ごとの売上の合計(手元の数量 × 単価)が請求書の税抜合計と一致する
        var shipped = await LoadOrderAsync(order.Id);
        Assert.Equal(SalesOrderStatus.Shipped, shipped.Status);
        Assert.Equal(invoice.Tax.TaxableTotal, shipped.Lines.Aggregate(Money.Zero, (total, l) => total + l.SalesAmount));
    }

    // 出荷の指示と確定を、別々の操作として行う(指示した出荷を読み直してから確定する)
    private async Task ShipAsync(TestScenario scenario, long orderId, TestScenario.MasterData m, DateOnly shippedOn, int bolts, int tea)
    {
        var order = await LoadOrderAsync(orderId);
        var lines = new List<OrderLineQuantity>();
        if (bolts > 0) lines.Add(new(order.Lines.Single(l => l.TaxRate == Standard), bolts));
        if (tea > 0) lines.Add(new(order.Lines.Single(l => l.TaxRate == Reduced), tea));
        var instructed = scenario.AddShipment(order.InstructShipment(lines), m.Warehouse);
        await scenario.EndRequestAsync();

        var shipment = await Db.Shipments
            .Include("_order.Lines")
            .Include(s => s.Lines).ThenInclude(l => l.OrderLine)
            .SingleAsync(s => s.Id == instructed.Id);
        scenario.AddSalesRecord(shipment.Confirm(shippedOn), m.Branch);
        await scenario.EndRequestAsync();
    }

    private Task<SalesOrder> LoadOrderAsync(long id) => Db.SalesOrders.Include(o => o.Lines).SingleAsync(o => o.Id == id);
}
