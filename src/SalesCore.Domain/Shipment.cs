namespace SalesCore.Domain;

/// <summary>出荷の状態。指示 → 確定 ／ 取消。</summary>
public enum ShipmentStatus
{
    /// <summary>指示</summary>
    Instructed,

    /// <summary>確定</summary>
    Confirmed,

    /// <summary>取消</summary>
    Cancelled,
}

/// <summary>
/// 出荷。受注の <see cref="SalesOrder.InstructShipment"/> からしか作れない。
/// 確定すると売上が計上される(出荷基準)。確定した出荷は取り消せず、打ち消すのは返品。
/// </summary>
public sealed class Shipment
{
    private readonly SalesOrder _order = null!;
    private readonly List<ShipmentLine> _lines = [];

    private Shipment()
    {
        // DB から読み込むとき(EF Core)に使う
    }

    internal Shipment(SalesOrder order, IReadOnlyList<OrderLineQuantity> lines)
    {
        _order = order;
        _lines = lines.Select(l => new ShipmentLine(l.Line, l.Quantity)).ToList();
    }

    public long Id { get; private set; }

    public IReadOnlyList<ShipmentLine> Lines => _lines;

    public ShipmentStatus Status { get; private set; } = ShipmentStatus.Instructed;

    /// <summary>出荷日(確定した日)。指示の段階では無い。</summary>
    public DateOnly? ShippedOn { get; private set; }

    /// <summary>出荷を確定し、売上を計上する。売上日は出荷確定日。</summary>
    public SalesRecord Confirm(DateOnly shippedOn)
    {
        RequireInstructed("確定");
        var lines = _lines.Select(l => new OrderLineQuantity(l.OrderLine, l.Quantity)).ToList();
        var record = _order.RecordShipment(this, shippedOn, lines); // 受注の側で止まれば、出荷は指示のまま
        Status = ShipmentStatus.Confirmed;
        ShippedOn = shippedOn;
        return record;
    }

    public void Cancel()
    {
        RequireInstructed("取消");
        Status = ShipmentStatus.Cancelled;
    }

    private void RequireInstructed(string operation)
    {
        if (Status is not ShipmentStatus.Instructed)
        {
            throw new InvalidOperationException($"状態が {Status} の出荷は{operation}できない。");
        }
    }
}

/// <summary>出荷の明細。どの受注明細を、いくつ出すか。</summary>
public sealed class ShipmentLine
{
    private ShipmentLine()
    {
        // DB から読み込むとき(EF Core)に使う
    }

    internal ShipmentLine(SalesOrderLine orderLine, decimal quantity)
    {
        OrderLine = orderLine;
        Quantity = quantity;
    }

    public long Id { get; private set; }

    public SalesOrderLine OrderLine { get; private set; } = null!;

    public decimal Quantity { get; private set; }
}
