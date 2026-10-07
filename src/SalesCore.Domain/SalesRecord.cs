namespace SalesCore.Domain;

/// <summary>売上の明細。返品は数量も金額も負。金額は累計差分で決まるので、単価 × 数量と1円違うことがある。</summary>
public sealed class SalesRecordLine
{
    private SalesRecordLine()
    {
        // DB から読み込むとき(EF Core)に使う
    }

    internal SalesRecordLine(SalesOrderLine orderLine, decimal quantity, decimal unitPrice, TaxRate taxRate, Money amount)
    {
        OrderLine = orderLine;
        Quantity = quantity;
        UnitPrice = unitPrice;
        TaxRate = taxRate;
        Amount = amount;
    }

    public long Id { get; private set; }

    /// <summary>この売上のもとになった受注明細。出荷でも返品でも指す。</summary>
    public SalesOrderLine OrderLine { get; private set; } = null!;

    public decimal Quantity { get; private set; }

    public decimal UnitPrice { get; private set; }

    public TaxRate TaxRate { get; private set; }

    /// <summary>税抜金額。</summary>
    public Money Amount { get; private set; }
}

/// <summary>
/// 売上。出荷の確定か返品からしか生まれない(コンストラクタを公開していない)。
/// 請求書は売上からしか作れないので、未出荷のまま請求するコードは書けない。
/// </summary>
public sealed class SalesRecord
{
    private readonly List<SalesRecordLine> _lines = [];

    private SalesRecord()
    {
        // DB から読み込むとき(EF Core)に使う
    }

    internal SalesRecord(DateOnly recordedOn, List<SalesRecordLine> lines, Shipment? shipment)
    {
        RecordedOn = recordedOn;
        _lines = lines;
        Shipment = shipment;
    }

    public long Id { get; private set; }

    /// <summary>売上日。出荷確定日(返品なら返品日)。</summary>
    public DateOnly RecordedOn { get; private set; }

    public IReadOnlyList<SalesRecordLine> Lines => _lines;

    /// <summary>この売上を計上した出荷。返品の売上には無い。</summary>
    public Shipment? Shipment { get; private set; }

    /// <summary>税抜金額の合計。</summary>
    public Money Amount => _lines.Aggregate(Money.Zero, (total, line) => total + line.Amount);
}
