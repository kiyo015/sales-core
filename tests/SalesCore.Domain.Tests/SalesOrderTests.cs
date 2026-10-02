namespace SalesCore.Domain.Tests;

/// <summary>
/// 受注の状態遷移と分納。規則の定義元は docs/domain/business-rules.md の「3. 販売の流れ」と
/// docs/domain/business-flow.md の「3. 受注の状態」。
/// 状態を飛び越えない・逆戻りしない・受注数量を超えて出荷しない・取消は状態で表す。
/// </summary>
public class SalesOrderTests
{
    private static readonly TaxRate Standard = TaxRate.Of(10m);
    private static readonly DateOnly Today = new(2026, 10, 2);

    private static SalesOrderLine Line(decimal quantity, decimal unitPrice = 100m) => new(quantity, unitPrice, Standard);

    private static SalesOrder Approved(params SalesOrderLine[] lines)
    {
        var order = new SalesOrder(lines);
        order.Approve();
        return order;
    }

    private static SalesRecord Ship(SalesOrder order, SalesOrderLine line, decimal quantity) =>
        order.InstructShipment([new(line, quantity)]).Confirm(Today);

    // ── 状態の進み方 ──
    [Fact]
    public void 登録した受注は受付で始まる()
    {
        var order = new SalesOrder([Line(10)]);

        Assert.Equal(SalesOrderStatus.Received, order.Status);
    }

    [Fact]
    public void 受付の受注を承認すると承認済になる()
    {
        var order = new SalesOrder([Line(10)]);

        order.Approve();

        Assert.Equal(SalesOrderStatus.Approved, order.Status);
    }

    [Fact]
    public void 出荷を指示しただけでは受注の状態も出荷済み数量も変わらない()
    {
        var line = Line(10);
        var order = Approved(line);

        var shipment = order.InstructShipment([new(line, 3)]);

        Assert.Equal(ShipmentStatus.Instructed, shipment.Status);
        Assert.Equal(SalesOrderStatus.Approved, order.Status);
        Assert.Equal(0m, line.ShippedQuantity);
    }

    // ── 分納 ──
    [Fact]
    public void 一部を出荷確定すると一部出荷になり_出荷済み数量が増える()
    {
        var line = Line(10);
        var order = Approved(line);

        Ship(order, line, 3);

        Assert.Equal(SalesOrderStatus.PartiallyShipped, order.Status);
        Assert.Equal(3m, line.ShippedQuantity);
        Assert.Equal(7m, line.RemainingQuantity);
    }

    [Fact]
    public void 残りをすべて出荷確定すると出荷済になる()
    {
        var line = Line(10);
        var order = Approved(line);

        Ship(order, line, 3);
        Ship(order, line, 6.5m);
        Assert.Equal(SalesOrderStatus.PartiallyShipped, order.Status);
        Ship(order, line, 0.5m);

        Assert.Equal(SalesOrderStatus.Shipped, order.Status);
        Assert.Equal(0m, line.RemainingQuantity);
    }

    [Fact]
    public void 全量を1回で出荷確定すると_一部出荷を経ずに出荷済になる()
    {
        var line = Line(10);
        var order = Approved(line);

        Ship(order, line, 10);

        Assert.Equal(SalesOrderStatus.Shipped, order.Status);
    }

    [Fact]
    public void 明細が複数あるとき_1行でも残りがあれば一部出荷()
    {
        var first = Line(10);
        var second = Line(5);
        var order = Approved(first, second);

        Ship(order, first, 10);
        Assert.Equal(SalesOrderStatus.PartiallyShipped, order.Status);

        Ship(order, second, 5);
        Assert.Equal(SalesOrderStatus.Shipped, order.Status);
    }

    // ── 受注数量を超える出荷はできない ──
    [Fact]
    public void 受注数量を超える出荷は指示できない()
    {
        var line = Line(10);
        var order = Approved(line);

        Assert.Throws<InvalidOperationException>(() => order.InstructShipment([new(line, 10.001m)]));
    }

    [Fact]
    public void 分納の累計が受注数量を超える出荷は指示できない()
    {
        var line = Line(10);
        var order = Approved(line);
        Ship(order, line, 6);

        Assert.Throws<InvalidOperationException>(() => order.InstructShipment([new(line, 5)]));
        Assert.Equal(6m, line.ShippedQuantity);
    }

    // 指示の時点ではどちらも残りの範囲内。先に確定したほうが通り、後のほうは確定の時点で止まる
    [Fact]
    public void 指示が2件あって合計が受注数量を超えるなら_後から確定するほうが止まる()
    {
        var line = Line(10);
        var order = Approved(line);
        var first = order.InstructShipment([new(line, 6)]);
        var second = order.InstructShipment([new(line, 6)]);

        first.Confirm(Today);

        Assert.Throws<InvalidOperationException>(() => second.Confirm(Today));
        Assert.Equal(6m, line.ShippedQuantity);
        Assert.Equal(ShipmentStatus.Instructed, second.Status);
    }

    // 1行目は範囲内、2行目が超過。1行目だけ出荷済みになると、売上の無い出荷済み数量が残る
    [Fact]
    public void 複数行の出荷で1行でも超過なら_どの行も出荷されない()
    {
        var first = Line(10);
        var second = Line(10);
        var order = Approved(first, second);
        var earlier = order.InstructShipment([new(first, 5), new(second, 8)]);
        var later = order.InstructShipment([new(first, 5), new(second, 8)]);
        earlier.Confirm(Today);

        Assert.Throws<InvalidOperationException>(() => later.Confirm(Today));

        Assert.Equal(5m, first.ShippedQuantity);
        Assert.Equal(8m, second.ShippedQuantity);
    }

    [Fact]
    public void 同じ明細を1回の出荷に2度入れられない()
    {
        var line = Line(10);
        var order = Approved(line);

        Assert.Throws<ArgumentException>(() => order.InstructShipment([new(line, 6), new(line, 6)]));
    }

    [Fact]
    public void 他の受注の明細は出荷できない()
    {
        var order = Approved(Line(10));
        var lineOfAnotherOrder = Line(10);
        _ = Approved(lineOfAnotherOrder);

        Assert.Throws<ArgumentException>(() => order.InstructShipment([new(lineOfAnotherOrder, 1)]));
    }

    [Fact]
    public void 明細の無い出荷は指示できない()
    {
        var order = Approved(Line(10));

        Assert.Throws<ArgumentException>(() => order.InstructShipment([]));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0.0001")] // 数量は小数3桁まで
    public void 数量が0以下や小数4桁以上の出荷は指示できない(string quantity)
    {
        var line = Line(10);
        var order = Approved(line);

        Assert.Throws<ArgumentException>(() => order.InstructShipment([new(line, decimal.Parse(quantity))]));
    }

    // ── 受注と明細の作り方 ──
    [Fact]
    public void 明細の無い受注は作れない()
    {
        Assert.Throws<ArgumentException>(() => new SalesOrder([]));
    }

    [Theory]
    [InlineData("0", "100")]
    [InlineData("-1", "100")]
    [InlineData("1.0001", "100")]  // 数量は小数3桁まで
    [InlineData("1", "-0.01")]     // 値引きの明細行は入力方法が未決定。決まるまで負の単価は受け付けない
    [InlineData("1", "100.001")]   // 単価は小数2桁まで
    public void 数量や単価が決まりから外れる明細は作れない(string quantity, string unitPrice)
    {
        Assert.Throws<ArgumentException>(
            () => new SalesOrderLine(decimal.Parse(quantity), decimal.Parse(unitPrice), Standard));
    }

    [Fact]
    public void 受注の金額は明細金額の合計で_明細金額は単価と数量の積の四捨五入()
    {
        var order = new SalesOrder([Line(1.5m, 33.33m), Line(2, 1.25m)]); // 49.995 → 50、2.5 → 3

        Assert.Equal(Money.Of(50), order.Lines[0].Amount);
        Assert.Equal(Money.Of(3), order.Lines[1].Amount);
        Assert.Equal(Money.Of(53), order.Amount);
    }

    // ── 取消は状態で表す ──
    [Fact]
    public void 取り消した受注は消えずに取消の状態で残る()
    {
        var order = Approved(Line(10));

        order.Cancel();

        Assert.Equal(SalesOrderStatus.Cancelled, order.Status);
        Assert.Single(order.Lines);
    }

    [Fact]
    public void 取り消した受注に出ていた出荷指示は確定できない()
    {
        var line = Line(10);
        var order = Approved(line);
        var shipment = order.InstructShipment([new(line, 3)]);
        order.Cancel();

        Assert.Throws<InvalidOperationException>(() => shipment.Confirm(Today));
        Assert.Equal(0m, line.ShippedQuantity);
        Assert.Equal(SalesOrderStatus.Cancelled, order.Status);
    }

    // ── 出荷の状態(指示 → 確定 ／ 取消) ──
    [Fact]
    public void 出荷を確定すると確定の状態になる()
    {
        var line = Line(10);
        var shipment = Approved(line).InstructShipment([new(line, 3)]);

        shipment.Confirm(Today);

        Assert.Equal(ShipmentStatus.Confirmed, shipment.Status);
    }

    // 2度確定できると、売上が二重に計上される
    [Fact]
    public void 確定した出荷はもう一度確定できない()
    {
        var line = Line(10);
        var shipment = Approved(line).InstructShipment([new(line, 3)]);
        shipment.Confirm(Today);

        Assert.Throws<InvalidOperationException>(() => shipment.Confirm(Today));
        Assert.Equal(3m, line.ShippedQuantity);
    }

    [Fact]
    public void 取り消した出荷指示は確定できず_受注にも影響しない()
    {
        var line = Line(10);
        var order = Approved(line);
        var shipment = order.InstructShipment([new(line, 3)]);

        shipment.Cancel();

        Assert.Equal(ShipmentStatus.Cancelled, shipment.Status);
        Assert.Throws<InvalidOperationException>(() => shipment.Confirm(Today));
        Assert.Equal(SalesOrderStatus.Approved, order.Status);
        Assert.Equal(0m, line.ShippedQuantity);
    }

    // 確定した出荷は売上になっている。打ち消すのは取消ではなく返品(赤伝)
    [Fact]
    public void 確定した出荷は取り消せない()
    {
        var line = Line(10);
        var shipment = Approved(line).InstructShipment([new(line, 3)]);
        shipment.Confirm(Today);

        Assert.Throws<InvalidOperationException>(() => shipment.Cancel());
        Assert.Equal(ShipmentStatus.Confirmed, shipment.Status);
    }

    // ── 状態 × 操作 の全組み合わせ ──
    // 許されない操作は例外になり、状態は変わらない。飛び越し(受付のまま出荷)も逆戻り(出荷済を承認・取消)もここで止まる
    [Theory]
    [InlineData(SalesOrderStatus.Received, "承認", true)]
    [InlineData(SalesOrderStatus.Received, "取消", true)]
    [InlineData(SalesOrderStatus.Received, "出荷指示", false)] // 承認を飛び越えない
    [InlineData(SalesOrderStatus.Received, "返品", false)]
    [InlineData(SalesOrderStatus.Approved, "承認", false)]
    [InlineData(SalesOrderStatus.Approved, "取消", true)]      // 未出荷のときだけ取り消せる
    [InlineData(SalesOrderStatus.Approved, "出荷指示", true)]
    [InlineData(SalesOrderStatus.Approved, "返品", false)]     // 出荷していないものは返品できない
    [InlineData(SalesOrderStatus.PartiallyShipped, "承認", false)]
    [InlineData(SalesOrderStatus.PartiallyShipped, "取消", false)] // 出荷後は取り消せない。赤伝で打ち消す
    [InlineData(SalesOrderStatus.PartiallyShipped, "出荷指示", true)]
    [InlineData(SalesOrderStatus.PartiallyShipped, "返品", true)]
    [InlineData(SalesOrderStatus.Shipped, "承認", false)]
    [InlineData(SalesOrderStatus.Shipped, "取消", false)]
    [InlineData(SalesOrderStatus.Shipped, "出荷指示", false)]
    [InlineData(SalesOrderStatus.Shipped, "返品", true)]
    [InlineData(SalesOrderStatus.Cancelled, "承認", false)]
    [InlineData(SalesOrderStatus.Cancelled, "取消", false)]
    [InlineData(SalesOrderStatus.Cancelled, "出荷指示", false)]
    [InlineData(SalesOrderStatus.Cancelled, "返品", false)]
    public void 状態ごとに許される操作だけができる(SalesOrderStatus status, string operation, bool allowed)
    {
        var line = Line(10);
        var order = new SalesOrder([line]);
        switch (status)
        {
            case SalesOrderStatus.Approved: order.Approve(); break;
            case SalesOrderStatus.PartiallyShipped: order.Approve(); Ship(order, line, 4); break;
            case SalesOrderStatus.Shipped: order.Approve(); Ship(order, line, 10); break;
            case SalesOrderStatus.Cancelled: order.Cancel(); break;
        }
        Assert.Equal(status, order.Status); // 前提: 狙った状態を作れている

        Action act = operation switch
        {
            "承認" => order.Approve,
            "取消" => order.Cancel,
            "出荷指示" => () => order.InstructShipment([new(line, 1)]),
            "返品" => () => order.RecordReturn(Today, [new(line, 1)]),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

        if (allowed)
        {
            act();

            // 通るだけでなく、決まった先の状態になっている。出荷指示と返品では受注の状態は動かない
            var expected = operation switch
            {
                "承認" => SalesOrderStatus.Approved,
                "取消" => SalesOrderStatus.Cancelled,
                _ => status,
            };
            Assert.Equal(expected, order.Status);
            Assert.Equal(operation == "返品" ? 1m : 0m, line.ReturnedQuantity);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(act);
            Assert.Equal(status, order.Status);
        }
    }
}
