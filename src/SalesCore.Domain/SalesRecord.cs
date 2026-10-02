namespace SalesCore.Domain;

/// <summary>売上の明細。返品は数量も金額も負。金額は累計差分で決まるので、単価 × 数量と1円違うことがある。</summary>
public sealed class SalesRecordLine
{
    internal SalesRecordLine(decimal quantity, decimal unitPrice, TaxRate taxRate, Money amount)
    {
        Quantity = quantity;
        UnitPrice = unitPrice;
        TaxRate = taxRate;
        Amount = amount;
    }

    public decimal Quantity { get; }

    public decimal UnitPrice { get; }

    public TaxRate TaxRate { get; }

    /// <summary>税抜金額。</summary>
    public Money Amount { get; }
}

/// <summary>
/// 売上。出荷の確定か返品からしか生まれない(コンストラクタを公開していない)。
/// 請求書は売上からしか作れないので、未出荷のまま請求するコードは書けない。
/// </summary>
public sealed class SalesRecord
{
    internal SalesRecord(DateOnly recordedOn, IReadOnlyList<SalesRecordLine> lines)
    {
        RecordedOn = recordedOn;
        Lines = lines;
    }

    /// <summary>売上日。出荷確定日(返品なら返品日)。</summary>
    public DateOnly RecordedOn { get; }

    public IReadOnlyList<SalesRecordLine> Lines { get; }

    /// <summary>税抜金額の合計。</summary>
    public Money Amount => Lines.Aggregate(Money.Zero, (total, line) => total + line.Amount);
}
