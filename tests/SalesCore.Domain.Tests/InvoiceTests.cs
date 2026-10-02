using System.Reflection;
using System.Runtime.CompilerServices;

namespace SalesCore.Domain.Tests;

/// <summary>
/// 請求は売上からしか作れない。売上は出荷確定(と返品)からしか生まれない。
/// 規則の定義元は docs/domain/business-rules.md の「状態」(未出荷のまま請求しない)。
/// 未出荷の受注を請求しようとするコードは、渡せる型が無いのでコンパイルできない。
/// </summary>
public class InvoiceTests
{
    private static readonly TaxRate Standard = TaxRate.Of(10m);
    private static readonly TaxRate Reduced = TaxRate.Of(8m);
    private static readonly DateOnly Today = new(2026, 10, 2);

    private static SalesOrder Approved(params SalesOrderLine[] lines)
    {
        var order = new SalesOrder(lines);
        order.Approve();
        return order;
    }

    private static SalesRecord Ship(SalesOrder order, SalesOrderLine line, decimal quantity) =>
        order.InstructShipment([new(line, quantity)]).Confirm(Today);

    // 105円の売上3件(分納)。売上ごとに税額を出すと 11円 × 3 = 33円。
    // 請求書では税率ごとに1回: 315 × 10% = 31.5 → 32円
    [Fact]
    public void 分納した売上を請求書にまとめ_税額は税率ごとに1回だけ丸める()
    {
        var line = new SalesOrderLine(3, 105m, Standard);
        var order = Approved(line);
        var records = new[] { Ship(order, line, 1), Ship(order, line, 1), Ship(order, line, 1) };

        var invoice = Invoice.Create(records);

        Assert.Equal(records, invoice.SalesRecords);
        var summary = Assert.Single(invoice.Tax.Summaries);
        Assert.Equal(new TaxSummary(Standard, Money.Of(315), Money.Of(32)), summary);
        Assert.Equal(Money.Of(347), invoice.Tax.TotalWithTax);
    }

    [Fact]
    public void 税率の違う明細は_請求書で税率ごとに集計される()
    {
        var standardLine = new SalesOrderLine(1, 1000m, Standard);
        var reducedLine = new SalesOrderLine(1, 580m, Reduced);
        var order = Approved(standardLine, reducedLine);
        var record = order.InstructShipment([new(standardLine, 1), new(reducedLine, 1)]).Confirm(Today);

        var invoice = Invoice.Create([record]);

        Assert.Collection(
            invoice.Tax.Summaries,
            s => Assert.Equal(new TaxSummary(Standard, Money.Of(1000), Money.Of(100)), s),
            s => Assert.Equal(new TaxSummary(Reduced, Money.Of(580), Money.Of(46)), s));
    }

    [Fact]
    public void 返品の売上を同じ請求書に入れると_対象額も税額も打ち消し合う()
    {
        var line = new SalesOrderLine(1, 105m, Standard);
        var order = Approved(line);
        var shipped = Ship(order, line, 1);
        var returned = order.RecordReturn(Today, [new(line, 1)]);

        var invoice = Invoice.Create([shipped, returned]);

        Assert.Equal(Money.Of(105), shipped.Amount);
        Assert.Equal(Money.Of(-105), returned.Amount);
        Assert.Equal(new TaxSummary(Standard, Money.Zero, Money.Zero), Assert.Single(invoice.Tax.Summaries));
        Assert.Equal(Money.Zero, invoice.Tax.TotalWithTax);
    }

    // ── 型による保証 ──
    // 売上・出荷・請求書に公開コンストラクタが無いので、外のコードは出荷確定を通さずに売上を作れない。
    // InternalsVisibleTo があると internal のコンストラクタが外から呼べてしまうので、付いていないことも確かめる
    [Theory]
    [InlineData(typeof(SalesRecord))]
    [InlineData(typeof(SalesRecordLine))]
    [InlineData(typeof(Shipment))]
    [InlineData(typeof(Invoice))]
    public void 売上と出荷と請求書は_外から直接は作れない(Type type)
    {
        Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.True(type.IsSealed); // 継承して作る抜け道も無い
        Assert.Empty(type.Assembly.GetCustomAttributes<InternalsVisibleToAttribute>());
    }

    [Fact]
    public void 請求書を作る入口は_売上を受け取るものだけ()
    {
        var factories = typeof(Invoice)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.ReturnType == typeof(Invoice));

        var parameter = Assert.Single(Assert.Single(factories).GetParameters());
        Assert.Equal(typeof(IEnumerable<SalesRecord>), parameter.ParameterType);
    }
}
