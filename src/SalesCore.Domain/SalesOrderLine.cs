namespace SalesCore.Domain;

/// <summary>
/// 受注明細。数量は小数3桁まで、単価は小数2桁まで(docs/domain/business-rules.md の「1. 金額と数量」)。
/// 出荷済み数量と返品数量を別々に持つ。出荷済み数量は増えるだけなので、受注の状態は逆戻りしない。
/// </summary>
public sealed class SalesOrderLine
{
    public SalesOrderLine(decimal quantity, decimal unitPrice, TaxRate taxRate)
    {
        EnsureQuantity(quantity, nameof(quantity));
        // 値引きの明細行(負の単価)は入力方法が未決定。決まるまで受け付けない
        if (unitPrice < 0m)
        {
            throw new ArgumentException($"単価は0以上にする({unitPrice})。", nameof(unitPrice));
        }
        if (unitPrice * 100m != decimal.Truncate(unitPrice * 100m))
        {
            throw new ArgumentException($"単価は小数2桁まで({unitPrice})。", nameof(unitPrice));
        }
        Quantity = quantity;
        UnitPrice = unitPrice;
        TaxRate = taxRate;
    }

    /// <summary>受注数量。</summary>
    public decimal Quantity { get; }

    /// <summary>受注時点の単価。</summary>
    public decimal UnitPrice { get; }

    /// <summary>受注時点の税率。</summary>
    public TaxRate TaxRate { get; }

    /// <summary>出荷済み数量(分納の累計)。返品しても減らない。</summary>
    public decimal ShippedQuantity { get; private set; }

    /// <summary>返品数量の累計。</summary>
    public decimal ReturnedQuantity { get; private set; }

    /// <summary>まだ出荷していない数量。返品しても増えない。</summary>
    public decimal RemainingQuantity => Quantity - ShippedQuantity;

    /// <summary>明細金額(税抜)。単価 × 受注数量を四捨五入。</summary>
    public Money Amount => AmountOf(Quantity);

    /// <summary>
    /// この明細の売上の合計(税抜)。得意先の手元にある数量(出荷済み − 返品) × 単価を四捨五入した金額で、
    /// 出荷と返品で計上した売上をすべて足したものと必ず一致する。
    /// </summary>
    public Money SalesAmount => AmountOf(ShippedQuantity - ReturnedQuantity);

    internal static void EnsureQuantity(decimal quantity, string paramName)
    {
        if (quantity <= 0m)
        {
            throw new ArgumentException($"数量は0より大きくする({quantity})。", paramName);
        }
        if (quantity * 1000m != decimal.Truncate(quantity * 1000m))
        {
            throw new ArgumentException($"数量は小数3桁まで({quantity})。", paramName);
        }
    }

    internal void EnsureCanShip(decimal quantity)
    {
        if (quantity > RemainingQuantity)
        {
            throw new InvalidOperationException(
                $"受注数量を超える出荷はできない(出荷 {quantity}、残り {RemainingQuantity})。");
        }
    }

    internal void EnsureCanReturn(decimal quantity)
    {
        if (quantity > ShippedQuantity - ReturnedQuantity)
        {
            throw new InvalidOperationException(
                $"出荷して手元にある数量を超える返品はできない(返品 {quantity}、手元 {ShippedQuantity - ReturnedQuantity})。");
        }
    }

    internal SalesRecordLine Ship(decimal quantity)
    {
        EnsureCanShip(quantity);
        var before = SalesAmount;
        ShippedQuantity += quantity;
        return new SalesRecordLine(quantity, UnitPrice, TaxRate, SalesAmount - before);
    }

    internal SalesRecordLine Return(decimal quantity)
    {
        EnsureCanReturn(quantity);
        var before = SalesAmount;
        ReturnedQuantity += quantity;
        return new SalesRecordLine(-quantity, UnitPrice, TaxRate, SalesAmount - before);
    }

    // 金額は「動いた分 × 単価」を丸めず、累計で丸めた金額の差を取る(累計差分)。
    // 33.33円 × 3個を1個ずつ丸めると 99円になり、一括の 100円と合わないため
    private Money AmountOf(decimal quantity) => Money.Round(UnitPrice * quantity);
}
