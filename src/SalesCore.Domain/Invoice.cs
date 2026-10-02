namespace SalesCore.Domain;

/// <summary>
/// 請求書。<b>売上からしか作れない。</b>受注や出荷指示を渡す入口は無い(未出荷のまま請求しない)。
/// 締め・繰越・状態(確定 → 発行済 ／ 取消)・二重請求の防止は Day31 で足す。
/// </summary>
public sealed class Invoice
{
    private Invoice(IReadOnlyList<SalesRecord> salesRecords, TaxBreakdown tax)
    {
        SalesRecords = salesRecords;
        Tax = tax;
    }

    public IReadOnlyList<SalesRecord> SalesRecords { get; }

    /// <summary>税率ごとの対象額と税額。売上ごとではなく、請求書全体で税率ごとに1回だけ丸める。</summary>
    public TaxBreakdown Tax { get; }

    public static Invoice Create(IEnumerable<SalesRecord> salesRecords)
    {
        var records = salesRecords.ToList();
        var lines = records.SelectMany(record => record.Lines).Select(line => new TaxableLine(line.Amount, line.TaxRate));
        return new Invoice(records, TaxCalculator.Calculate(lines));
    }
}
