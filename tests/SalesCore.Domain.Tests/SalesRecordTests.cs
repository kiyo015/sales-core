namespace SalesCore.Domain.Tests;

/// <summary>
/// 売上の計上と、分納・返品の金額(累計差分)。規則の定義元は docs/domain/business-rules.md の
/// 「端数処理」「分納と按分の端数」「売上の計上」。
/// 出荷と返品をどう並べても、売上の合計は「得意先の手元にある数量 × 単価」の四捨五入に一致する。
/// </summary>
public class SalesRecordTests
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

    private static SalesRecord Return(SalesOrder order, SalesOrderLine line, decimal quantity) =>
        order.RecordReturn(Today, [new(line, quantity)]);

    // ── 売上の計上(出荷基準) ──
    [Fact]
    public void 出荷を確定すると売上が計上され_売上日は出荷確定日になる()
    {
        var line = new SalesOrderLine(10, 1980m, Reduced);
        var order = Approved(line);

        var record = order.InstructShipment([new(line, 3)]).Confirm(new DateOnly(2026, 10, 15));

        Assert.Equal(new DateOnly(2026, 10, 15), record.RecordedOn);
        var recordLine = Assert.Single(record.Lines);
        Assert.Equal(3m, recordLine.Quantity);
        Assert.Equal(1980m, recordLine.UnitPrice);
        Assert.Equal(Reduced, recordLine.TaxRate);
        Assert.Equal(Money.Of(5940), recordLine.Amount);
        Assert.Equal(Money.Of(5940), record.Amount);
    }

    [Fact]
    public void 複数行の出荷は_1件の売上に行ごとの明細を持つ()
    {
        var first = new SalesOrderLine(10, 100m, Standard);
        var second = new SalesOrderLine(5, 200m, Reduced);
        var order = Approved(first, second);

        var record = order.InstructShipment([new(first, 2), new(second, 5)]).Confirm(Today);

        Assert.Collection(
            record.Lines,
            l => Assert.Equal((2m, Standard, Money.Of(200)), (l.Quantity, l.TaxRate, l.Amount)),
            l => Assert.Equal((5m, Reduced, Money.Of(1000)), (l.Quantity, l.TaxRate, l.Amount)));
        Assert.Equal(Money.Of(1200), record.Amount);
    }

    // ── 分納の金額は累計差分 ──
    // 33.33円 × 3個 = 99.99 → 100円。1個ずつ丸めると 33円 × 3 = 99円で合わない。
    // 累計で丸めた金額の差を取る: 33 → 67 → 100 なので 33・34・33円
    [Fact]
    public void 分納の金額は累計数量で丸めた金額の差で決まり_合計が明細金額に一致する()
    {
        var line = new SalesOrderLine(3, 33.33m, Standard);
        var order = Approved(line);

        var amounts = new[] { Ship(order, line, 1), Ship(order, line, 1), Ship(order, line, 1) }
            .Select(record => record.Amount)
            .ToArray();

        Assert.Equal(new[] { Money.Of(33), Money.Of(34), Money.Of(33) }, amounts);
        Assert.Equal(Money.Of(100), line.Amount);
        Assert.Equal(line.Amount, line.SalesAmount);
    }

    // ── 返品 ──
    [Fact]
    public void 返品の売上は数量も金額も負で_累計差分で決まる()
    {
        var line = new SalesOrderLine(3, 33.33m, Standard);
        var order = Approved(line);
        Ship(order, line, 3);

        var returns = new[] { Return(order, line, 1), Return(order, line, 1), Return(order, line, 1) };

        Assert.Equal(new[] { Money.Of(-33), Money.Of(-34), Money.Of(-33) }, returns.Select(r => r.Amount).ToArray());
        Assert.All(returns, r => Assert.Equal(-1m, Assert.Single(r.Lines).Quantity));
        Assert.Equal(Money.Zero, line.SalesAmount); // 全部返品すれば売上は0円。1円も残らない
    }

    [Fact]
    public void 返品しても受注の状態と出荷済み数量は戻らない()
    {
        var line = new SalesOrderLine(10, 100m, Standard);
        var order = Approved(line);
        Ship(order, line, 10);

        Return(order, line, 4);

        Assert.Equal(SalesOrderStatus.Shipped, order.Status);
        Assert.Equal(10m, line.ShippedQuantity);
        Assert.Equal(4m, line.ReturnedQuantity);
        Assert.Equal(Money.Of(600), line.SalesAmount);
    }

    [Fact]
    public void 出荷して手元にある数量を超える返品はできない()
    {
        var line = new SalesOrderLine(10, 100m, Standard);
        var order = Approved(line);
        Ship(order, line, 5);
        Return(order, line, 3);

        Assert.Throws<InvalidOperationException>(() => Return(order, line, 2.001m));
        Assert.Equal(3m, line.ReturnedQuantity);
    }

    // 返品で受注の残りは増えない。代わりの品を出すなら新しい受注を立てる
    [Fact]
    public void 返品した数量は同じ受注で出荷し直せない()
    {
        var line = new SalesOrderLine(10, 100m, Standard);
        var order = Approved(line);
        Ship(order, line, 10);
        Return(order, line, 3);

        Assert.Throws<InvalidOperationException>(() => order.InstructShipment([new(line, 3)]));
    }

    [Fact]
    public void 一部出荷の受注で返品があっても_残りは出荷できる()
    {
        var line = new SalesOrderLine(10, 33.33m, Standard);
        var order = Approved(line);
        Ship(order, line, 4);
        Return(order, line, 2);

        Ship(order, line, 6);

        Assert.Equal(SalesOrderStatus.Shipped, order.Status);
        Assert.Equal(Money.Of(267), line.SalesAmount); // 手元は 8個。33.33 × 8 = 266.64 → 267
    }

    [Fact]
    public void 複数行の返品で1行でも超過なら_どの行も返品されない()
    {
        var first = new SalesOrderLine(10, 100m, Standard);
        var second = new SalesOrderLine(10, 100m, Standard);
        var order = Approved(first, second);
        order.InstructShipment([new(first, 5), new(second, 5)]).Confirm(Today);

        Assert.Throws<InvalidOperationException>(() => order.RecordReturn(Today, [new(first, 5), new(second, 6)]));

        Assert.Equal(0m, first.ReturnedQuantity);
        Assert.Equal(0m, second.ReturnedQuantity);
    }

    // ── ランダムな出荷と返品の並び 1万件 ──
    // 期待値は製品コードと別の方法(long だけの整数計算)で出す。単価は銭、数量は 1/1000 の整数で持つ。
    // 乱数の種は固定。落ちたときに同じ並びで再現できる
    [Fact]
    public void ランダムな出荷と返品の並びでも_売上の合計は常に手元の数量と単価の積の四捨五入に一致する()
    {
        var random = new Random(20261002);
        int shipments = 0, returns = 0, oneYenOff = 0, fullyShipped = 0, fullyReturned = 0;

        for (var n = 0; n < 10_000; n++)
        {
            long unitPriceSen = random.Next(0, 1_000_000); // 0.00〜9,999.99円
            long ordered = random.Next(1, 20_001);         // 0.001〜20.000
            var line = new SalesOrderLine(ordered / 1000m, unitPriceSen / 100m, Standard);
            var order = Approved(line);
            var records = new List<SalesRecord>();
            long shipped = 0, returned = 0, salesTotal = 0;

            var moves = random.Next(1, 13);
            for (var move = 0; move < moves; move++)
            {
                var remaining = ordered - shipped;
                var onHand = shipped - returned;
                if (remaining == 0 && onHand == 0)
                {
                    break; // 全部出荷して全部返品された。もう動かせない
                }

                SalesRecord record;
                long quantity;
                if (remaining > 0 && (onHand == 0 || random.Next(2) == 0))
                {
                    quantity = random.Next(4) == 0 ? remaining : random.NextInt64(1, remaining + 1);
                    record = Ship(order, line, quantity / 1000m);
                    shipped += quantity;
                    shipments++;
                }
                else
                {
                    quantity = random.Next(4) == 0 ? onHand : random.NextInt64(1, onHand + 1);
                    record = Return(order, line, quantity / 1000m);
                    returned += quantity;
                    returns++;
                }

                // 動いた分だけを丸めた金額(採らなかった方式)と1円違う動きを数える
                if (Math.Abs((long)record.Amount.Yen) != RoundHalfUp(unitPriceSen * quantity, 100_000))
                {
                    oneYenOff++;
                }

                records.Add(record);
                salesTotal += (long)record.Amount.Yen;
                var expected = RoundHalfUp(unitPriceSen * (shipped - returned), 100_000);
                Assert.Equal(expected, salesTotal);
                Assert.Equal(expected, (long)line.SalesAmount.Yen);
                Assert.Equal(
                    shipped == ordered ? SalesOrderStatus.Shipped : SalesOrderStatus.PartiallyShipped,
                    order.Status);
            }

            // 請求書にまとめても同じ金額になる
            Assert.Equal(salesTotal, (long)Invoice.Create(records).Tax.TaxableTotal.Yen);
            fullyShipped += shipped == ordered ? 1 : 0;
            fullyReturned += shipped == returned ? 1 : 0;
        }

        // 乱数が偏って、確かめたい場面を通らないまま合格していないこと(Day21 の教訓)。
        // 2026-10-02 の実測: 出荷 24,688・返品 26,849・1円違う動き 8,089・出荷済まで 6,368件・全部返品 4,368件
        Assert.True(
            shipments > 10_000 && returns > 10_000 && oneYenOff > 1_000 && fullyShipped > 1_000 && fullyReturned > 1_000,
            $"出荷 {shipments} 返品 {returns} 1円違う動き {oneYenOff} 出荷済まで進んだ受注 {fullyShipped} 全部返品された受注 {fullyReturned}");
    }

    // 整数だけの四捨五入(0以上の値)。decimal も Money も使わない
    private static long RoundHalfUp(long numerator, long denominator) =>
        (numerator * 2 + denominator) / (denominator * 2);
}
