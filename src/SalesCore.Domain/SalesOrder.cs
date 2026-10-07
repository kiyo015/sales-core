namespace SalesCore.Domain;

/// <summary>受注の状態。受付 → 承認済 → 一部出荷 → 出荷済 ／ 取消。承認待ちは段階2で足す。</summary>
public enum SalesOrderStatus
{
    /// <summary>受付</summary>
    Received,

    /// <summary>承認済</summary>
    Approved,

    /// <summary>一部出荷</summary>
    PartiallyShipped,

    /// <summary>出荷済</summary>
    Shipped,

    /// <summary>取消</summary>
    Cancelled,
}

/// <summary>受注明細と、その明細について動かす数量(出荷・返品)の組。</summary>
public readonly record struct OrderLineQuantity(SalesOrderLine Line, decimal Quantity);

/// <summary>
/// 受注。状態は飛び越えも逆戻りもしない(docs/domain/business-flow.md の「3. 受注の状態」)。
/// 取消は状態で表し、削除しない。出荷済みの分を打ち消すのは取消ではなく返品(赤伝)。
/// </summary>
public sealed class SalesOrder
{
    private readonly List<SalesOrderLine> _lines = [];

    private SalesOrder()
    {
        // DB から読み込むとき(EF Core)に使う
    }

    public SalesOrder(IEnumerable<SalesOrderLine> lines)
    {
        _lines = lines.ToList();
        if (_lines.Count == 0)
        {
            throw new ArgumentException("受注には明細が1行以上要る。", nameof(lines));
        }
    }

    public long Id { get; private set; }

    public IReadOnlyList<SalesOrderLine> Lines => _lines;

    public SalesOrderStatus Status { get; private set; } = SalesOrderStatus.Received;

    /// <summary>税抜合計(明細金額の合計)。</summary>
    public Money Amount => _lines.Aggregate(Money.Zero, (total, line) => total + line.Amount);

    public void Approve()
    {
        Require(Status is SalesOrderStatus.Received, "承認");
        Status = SalesOrderStatus.Approved;
    }

    /// <summary>取り消せるのは未出荷のときだけ。</summary>
    public void Cancel()
    {
        Require(Status is SalesOrderStatus.Received or SalesOrderStatus.Approved, "取消");
        Status = SalesOrderStatus.Cancelled;
    }

    /// <summary>出荷を指示する。この時点では出荷済み数量も状態も変わらない(変わるのは確定のとき)。</summary>
    public Shipment InstructShipment(IEnumerable<OrderLineQuantity> lines)
    {
        var shipmentLines = Validate(lines);
        EnsureCanShip(shipmentLines);
        return new Shipment(this, shipmentLines);
    }

    /// <summary>
    /// 返品を売上(数量・金額が負)として計上する。受注の状態と出荷済み数量は戻らず、
    /// 返品した数量を同じ受注で出荷し直すこともできない。
    /// </summary>
    public SalesRecord RecordReturn(DateOnly returnedOn, IEnumerable<OrderLineQuantity> lines)
    {
        var returnLines = Validate(lines);
        foreach (var (line, quantity) in returnLines)
        {
            line.EnsureCanReturn(quantity);
        }
        return new SalesRecord(returnedOn, returnLines.Select(l => l.Line.Return(l.Quantity)).ToList(), shipment: null);
    }

    /// <summary>出荷の確定(<see cref="Shipment.Confirm"/>)から呼ばれる。売上を計上し、状態を進める。</summary>
    internal SalesRecord RecordShipment(Shipment shipment, DateOnly shippedOn, IReadOnlyList<OrderLineQuantity> lines)
    {
        // 全行を確かめてから動かす。途中の行で止まると、売上の無い出荷済み数量が残る
        EnsureCanShip(lines);
        var record = new SalesRecord(shippedOn, lines.Select(l => l.Line.Ship(l.Quantity)).ToList(), shipment);
        Status = _lines.All(line => line.RemainingQuantity == 0m)
            ? SalesOrderStatus.Shipped
            : SalesOrderStatus.PartiallyShipped;
        return record;
    }

    // 指示のときと確定のときの両方で確かめる。指示の後に受注が取り消されたり、別の出荷が先に確定したりするため
    private void EnsureCanShip(IReadOnlyList<OrderLineQuantity> lines)
    {
        Require(Status is SalesOrderStatus.Approved or SalesOrderStatus.PartiallyShipped, "出荷");
        foreach (var (line, quantity) in lines)
        {
            line.EnsureCanShip(quantity);
        }
    }

    private List<OrderLineQuantity> Validate(IEnumerable<OrderLineQuantity> lines)
    {
        var list = lines.ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("明細が1行以上要る。", nameof(lines));
        }
        if (list.Any(l => !_lines.Contains(l.Line)))
        {
            throw new ArgumentException("この受注の明細ではない。", nameof(lines));
        }
        if (list.Select(l => l.Line).Distinct().Count() != list.Count)
        {
            throw new ArgumentException("同じ明細が2度入っている。", nameof(lines));
        }
        foreach (var (_, quantity) in list)
        {
            SalesOrderLine.EnsureQuantity(quantity, nameof(lines));
        }
        return list;
    }

    private void Require(bool allowed, string operation)
    {
        if (!allowed)
        {
            throw new InvalidOperationException($"状態が {Status} の受注は{operation}できない。");
        }
    }
}
