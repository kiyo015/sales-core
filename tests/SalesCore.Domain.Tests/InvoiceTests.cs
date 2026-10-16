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
    private static readonly DateOnly PeriodFrom = new(2026, 10, 1);
    private static readonly DateOnly ClosingDate = new(2026, 10, 31);

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

        var invoice = Invoice.Create(PeriodFrom, ClosingDate, records);

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

        var invoice = Invoice.Create(PeriodFrom, ClosingDate, [record]);

        Assert.Collection(
            invoice.Tax.Summaries,
            s => Assert.Equal(new TaxSummary(Standard, Money.Of(1000), Money.Of(100)), s),
            s => Assert.Equal(new TaxSummary(Reduced, Money.Of(580), Money.Of(46)), s));
    }

    // 2個出荷して1個返品。返品の売上(負)は同じ税率の対象額から差し引き、税額は差し引いた後に1回だけ丸める
    [Fact]
    public void 返品の売上を同じ請求書に入れると_対象額も税額も差し引かれる()
    {
        var line = new SalesOrderLine(2, 105m, Standard);
        var order = Approved(line);
        var shipped = Ship(order, line, 2);
        var returned = order.RecordReturn(Today, [new(line, 1)]);

        var invoice = Invoice.Create(PeriodFrom, ClosingDate, [shipped, returned]);

        Assert.Equal(Money.Of(210), shipped.Amount);
        Assert.Equal(Money.Of(-105), returned.Amount);
        Assert.Equal(new TaxSummary(Standard, Money.Of(105), Money.Of(11)), Assert.Single(invoice.Tax.Summaries)); // 10.5 → 11
        Assert.Equal(Money.Of(116), invoice.Tax.TotalWithTax);
    }

    // 請求額が0円以下なら締めず、売上は未請求のまま次の締めに回す(2026-10-16 決定)。
    // 繰越の無い段階1でも、返品の分を次の請求から差し引ける
    [Fact]
    public void 返品だけで請求額がマイナスなら作らず_売上は次の締めに回る()
    {
        var line = new SalesOrderLine(1, 105m, Standard);
        var order = Approved(line);
        var october = Ship(order, line, 1);
        Invoice.Create(PeriodFrom, ClosingDate, [october]);
        var returned = order.RecordReturn(Today, [new(line, 1)]);

        var error = Assert.Throws<BusinessRuleException>(() => Invoice.Create(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), [returned]));

        Assert.Contains("次の締め", error.Message);
        Assert.Null(returned.Invoice);
    }

    [Fact]
    public void 出荷と全数返品が同じ期間で請求額が0円なら作らない()
    {
        var line = new SalesOrderLine(1, 105m, Standard);
        var order = Approved(line);
        var shipped = Ship(order, line, 1);
        var returned = order.RecordReturn(Today, [new(line, 1)]);

        Assert.Throws<BusinessRuleException>(() => Invoice.Create(PeriodFrom, ClosingDate, [shipped, returned]));
        Assert.Null(shipped.Invoice);
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

    // 締めの期間(日付)のほかに受け取るのは売上だけ。受注や出荷指示を渡す入口は無い
    [Fact]
    public void 請求書を作る入口は_売上を受け取るものだけ()
    {
        var factories = typeof(Invoice)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.ReturnType == typeof(Invoice));

        var parameter = Assert.Single(Assert.Single(factories).GetParameters(), p => p.ParameterType != typeof(DateOnly));
        Assert.Equal(typeof(IEnumerable<SalesRecord>), parameter.ParameterType);
    }

    // ── 締め(業務ルール集「4. 請求」)──

    // 段階1は入金が無いので繰越は0(2026-10-16 決定)。請求額 = 今回売上 + 消費税
    [Fact]
    public void 請求額は今回売上と消費税の合計で_繰越は0()
    {
        var line = new SalesOrderLine(3, 105m, Standard);
        var order = Approved(line);

        var invoice = Invoice.Create(PeriodFrom, ClosingDate, [Ship(order, line, 3)]);

        Assert.Equal((PeriodFrom, ClosingDate, ClosingDate), (invoice.PeriodFrom, invoice.PeriodTo, invoice.ClosingDate));
        Assert.Equal((Money.Zero, Money.Zero, Money.Zero), (invoice.PreviousBalance, invoice.ReceivedAmount, invoice.CarriedOver));
        Assert.Equal((Money.Of(315), Money.Of(32), Money.Of(347)), (invoice.SalesAmount, invoice.TaxAmount, invoice.TotalAmount));
        Assert.Equal(InvoiceStatus.Confirmed, invoice.Status);
    }

    [Fact]
    public void 請求書に入れた売上には請求書が付き_同じ売上は二重に請求できない()
    {
        var line = new SalesOrderLine(2, 105m, Standard);
        var order = Approved(line);
        var first = Ship(order, line, 1);
        var invoice = Invoice.Create(PeriodFrom, ClosingDate, [first]);

        Assert.Same(invoice, first.Invoice);
        var error = Assert.Throws<BusinessRuleException>(
            () => Invoice.Create(PeriodFrom, ClosingDate, [first, Ship(order, line, 1)]));
        Assert.Contains("二重に請求しない", error.Message);
    }

    [Fact]
    public void 期間の開始は締め日より後にできない()
    {
        var line = new SalesOrderLine(1, 105m, Standard);
        var order = Approved(line);

        Assert.Throws<ArgumentException>(() => Invoice.Create(ClosingDate.AddDays(1), ClosingDate, [Ship(order, line, 1)]));
    }

    [Fact]
    public void 請求する売上が無ければ請求書を作らない()
    {
        Assert.Throws<BusinessRuleException>(() => Invoice.Create(PeriodFrom, ClosingDate, []));
    }

    [Fact]
    public void 締め日より後の売上は入れられない()
    {
        var line = new SalesOrderLine(1, 105m, Standard);
        var order = Approved(line);
        var november = order.InstructShipment([new(line, 1)]).Confirm(new DateOnly(2026, 11, 1));

        Assert.Throws<BusinessRuleException>(() => Invoice.Create(PeriodFrom, ClosingDate, [november]));
        Assert.Null(november.Invoice);
    }

    // 締め日までの未請求の売上は全部入れる(2026-10-16 決定)。期間の前の売上でも、入れ忘れていれば次の締めで拾う
    [Fact]
    public void 期間より前の未請求の売上も入れられる()
    {
        var line = new SalesOrderLine(1, 105m, Standard);
        var order = Approved(line);
        var september = order.InstructShipment([new(line, 1)]).Confirm(new DateOnly(2026, 9, 20));

        var invoice = Invoice.Create(PeriodFrom, ClosingDate, [september]);

        Assert.Equal(Money.Of(105), invoice.SalesAmount);
    }
}
