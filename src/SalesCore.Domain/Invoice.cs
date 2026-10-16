namespace SalesCore.Domain;

/// <summary>請求書の状態。確定 → 発行済(帳票を作る段階5)／取消。段階1では確定だけを使う。</summary>
public enum InvoiceStatus
{
    /// <summary>確定</summary>
    Confirmed,

    /// <summary>発行済</summary>
    Issued,

    /// <summary>取消</summary>
    Cancelled,
}

/// <summary>
/// 請求書。<b>売上からしか作れない。</b>受注や出荷指示を渡す入口は無い(未出荷のまま請求しない)。
/// </summary>
public sealed class Invoice
{
    private readonly List<SalesRecord> _salesRecords = [];
    private readonly List<InvoiceTaxSummary> _taxSummaries = [];

    private Invoice()
    {
        // DB から読み込むとき(EF Core)に使う
    }

    private Invoice(DateOnly periodFrom, DateOnly closingDate, List<SalesRecord> salesRecords)
    {
        PeriodFrom = periodFrom;
        PeriodTo = closingDate;
        ClosingDate = closingDate;
        _salesRecords = salesRecords;

        var tax = TaxCalculator.Calculate(salesRecords.SelectMany(r => r.Lines).Select(l => new TaxableLine(l.Amount, l.TaxRate)));
        _taxSummaries = tax.Summaries.Select(s => new InvoiceTaxSummary(s)).ToList();
        // 段階1は入金(段階3)が無いので繰越しない(2026-10-16 決定)。前回請求額・入金額・繰越額は 0
        PreviousBalance = Money.Zero;
        ReceivedAmount = Money.Zero;
        CarriedOver = PreviousBalance - ReceivedAmount;
        SalesAmount = tax.TaxableTotal;
        TaxAmount = tax.TaxTotal;
        TotalAmount = CarriedOver + SalesAmount + TaxAmount;
        Status = InvoiceStatus.Confirmed;
    }

    public long Id { get; private set; }

    public DateOnly ClosingDate { get; private set; }

    public DateOnly PeriodFrom { get; private set; }

    public DateOnly PeriodTo { get; private set; }

    public Money PreviousBalance { get; private set; }

    public Money ReceivedAmount { get; private set; }

    public Money CarriedOver { get; private set; }

    public Money SalesAmount { get; private set; }

    public Money TaxAmount { get; private set; }

    public Money TotalAmount { get; private set; }

    public InvoiceStatus Status { get; private set; }

    public IReadOnlyList<SalesRecord> SalesRecords => _salesRecords;

    /// <summary>税率ごとの対象額と税額。売上ごとではなく、請求書全体で税率ごとに1回だけ丸める。</summary>
    // DB から読んだ子の行は順序が決まらないので、ここで税率の高い順に並べる(業務ルール集「2. 消費税」。2026-10-16 の AIレビュー)
    public TaxBreakdown Tax => new(_taxSummaries
        .OrderByDescending(s => s.TaxRate.Percent)
        .Select(s => new TaxSummary(s.TaxRate, s.TaxableAmount, s.TaxAmount))
        .ToList());

    /// <summary>
    /// 締めて請求書を作る。渡した売上には、この請求書が付く(同じ売上を二重に請求しない)。
    /// 締め日までの未請求の売上は、期間より前の日付でも入れてよい(入れ忘れを次の締めで拾う。2026-10-16 決定)。
    /// </summary>
    public static Invoice Create(DateOnly periodFrom, DateOnly closingDate, IEnumerable<SalesRecord> salesRecords)
    {
        if (periodFrom > closingDate)
        {
            throw new ArgumentException($"期間の開始 {periodFrom:yyyy-MM-dd} が締め日 {closingDate:yyyy-MM-dd} より後。", nameof(periodFrom));
        }
        var records = salesRecords.ToList();
        if (records.Count == 0)
        {
            throw new BusinessRuleException($"{closingDate:yyyy-MM-dd} 締めで請求する売上が無い。");
        }
        if (records.FirstOrDefault(r => r.Invoice is not null) is { } billed)
        {
            throw new BusinessRuleException($"売上 {billed.Id} は締め済み(請求書 {billed.Invoice!.Id})。二重に請求しない。");
        }
        if (records.FirstOrDefault(r => r.RecordedOn > closingDate) is { } later)
        {
            throw new BusinessRuleException($"売上日 {later.RecordedOn:yyyy-MM-dd} の売上は、{closingDate:yyyy-MM-dd} 締めに入れられない。");
        }

        var invoice = new Invoice(periodFrom, closingDate, records);
        // 請求額が0円以下(返品だけ、出荷と全数返品など)なら締めない。売上は未請求のまま次の締めに回し、
        // 次の請求から差し引く(繰越の無い段階1でも返品の分を失わない。2026-10-16 決定)
        if (invoice.TotalAmount.Yen <= 0m)
        {
            throw new BusinessRuleException(
                $"{closingDate:yyyy-MM-dd} 締めの請求額が {invoice.TotalAmount.Yen} 円で、0円以下なので締めない。売上は次の締めに回す。");
        }
        foreach (var record in records)
        {
            record.BillTo(invoice);
        }
        return invoice;
    }
}

/// <summary>請求書の税率ごとの集計(invoice_tax_summaries の1行)。</summary>
public sealed class InvoiceTaxSummary
{
    private InvoiceTaxSummary()
    {
        // DB から読み込むとき(EF Core)に使う
    }

    internal InvoiceTaxSummary(TaxSummary summary)
    {
        TaxRate = summary.Rate;
        TaxableAmount = summary.TaxableAmount;
        TaxAmount = summary.Tax;
    }

    public long Id { get; private set; }

    public TaxRate TaxRate { get; private set; }

    /// <summary>税率ごとの対象額(税抜)。</summary>
    public Money TaxableAmount { get; private set; }

    /// <summary>税率ごとに1回だけ四捨五入した税額。</summary>
    public Money TaxAmount { get; private set; }
}
