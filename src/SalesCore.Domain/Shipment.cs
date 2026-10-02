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
    private readonly SalesOrder _order;

    internal Shipment(SalesOrder order, IReadOnlyList<OrderLineQuantity> lines)
    {
        _order = order;
        Lines = lines;
    }

    public IReadOnlyList<OrderLineQuantity> Lines { get; }

    public ShipmentStatus Status { get; private set; } = ShipmentStatus.Instructed;

    /// <summary>出荷を確定し、売上を計上する。売上日は出荷確定日。</summary>
    public SalesRecord Confirm(DateOnly shippedOn)
    {
        RequireInstructed("確定");
        var record = _order.RecordShipment(shippedOn, Lines); // 受注の側で止まれば、出荷は指示のまま
        Status = ShipmentStatus.Confirmed;
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
