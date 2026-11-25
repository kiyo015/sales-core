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

    private Invoice(Customer billingCustomer, DateOnly periodFrom, DateOnly closingDate, List<SalesRecord> salesRecords, Money previousBalance, Money receivedAmount)
    {
        BillingCustomer = billingCustomer;
        PeriodFrom = periodFrom;
        PeriodTo = closingDate;
        ClosingDate = closingDate;
        _salesRecords = salesRecords;

        var tax = TaxCalculator.Calculate(salesRecords.SelectMany(r => r.Lines).Select(l => new TaxableLine(l.Amount, l.TaxRate)));
        _taxSummaries = tax.Summaries.Select(s => new InvoiceTaxSummary(s)).ToList();
        // 繰越の形式(2026-11-25 から)。過入金なら繰越は負になり、今回の請求から差し引く
        PreviousBalance = previousBalance;
        ReceivedAmount = receivedAmount;
        CarriedOver = PreviousBalance - ReceivedAmount;
        SalesAmount = tax.TaxableTotal;
        TaxAmount = tax.TaxTotal;
        TotalAmount = CarriedOver + SalesAmount + TaxAmount;
        DueDate = billingCustomer.DueDateFor(closingDate); // 支店の売上でも、請求先の回収条件
        Status = InvoiceStatus.Confirmed;
    }

    public long Id { get; private set; }

    /// <summary>請求先(請求先を持たない得意先)。</summary>
    public Customer BillingCustomer { get; private set; } = null!;

    public DateOnly ClosingDate { get; private set; }

    public DateOnly PeriodFrom { get; private set; }

    public DateOnly PeriodTo { get; private set; }

    /// <summary>前回請求額(前回の請求書の請求額。最初の請求書は0)。</summary>
    public Money PreviousBalance { get; private set; }

    /// <summary>入金額。まだどの請求書にも数えていない入金(打ち消しの入金は負)の合計(2026-11-25 本人の決定)。</summary>
    public Money ReceivedAmount { get; private set; }

    public Money CarriedOver { get; private set; }

    public Money SalesAmount { get; private set; }

    public Money TaxAmount { get; private set; }

    public Money TotalAmount { get; private set; }

    public InvoiceStatus Status { get; private set; }

    /// <summary>支払期限。請求先の回収条件から決める。2026-11-25 より前に締めた請求書は空。</summary>
    public DateOnly? DueDate { get; private set; }

    /// <summary>
    /// 今回分(今回売上＋消費税)。入金を充てる上限。繰越(前回請求額 − 入金額)は前の請求書で消し込むので、ここに入れない
    /// (同じ未回収を2回消し込まない。2026-11-20 本人の決定)。
    /// </summary>
    public Money ChargedAmount => SalesAmount + TaxAmount;

    /// <summary>
    /// 消し込んだ額(割当の合計)。割当を読み込まなくても分かるように、請求書の行に持つ。
    /// 充てるたびにこの行を更新するので、同じ請求書に2人が同時に充てると版数で止まる。DB の検査制約 ck_invoices_allocated でも0以上・今回分以下に止める。
    /// </summary>
    public Money AllocatedAmount { get; private set; }

    /// <summary>未回収の額(今回分のうち、まだ消し込んでいない分)。</summary>
    public Money Outstanding => ChargedAmount - AllocatedAmount;

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
    /// 請求額は繰越の形式(業務ルール集「4. 請求」の「繰越の締め」。2026-11-25 本人の決定)。
    /// </summary>
    /// <param name="earlierInvoices">請求先のこれまでの請求書(全部)。締め日の一番新しいものの請求額が前回請求額。</param>
    /// <param name="receipts">請求先の入金(全部。打ち消しの入金も)。締め日以前の入金日の合計から、前の請求書で数えた入金額を引いたものが入金額。</param>
    public static Invoice Create(Customer billingCustomer, DateOnly periodFrom, DateOnly closingDate, IEnumerable<SalesRecord> salesRecords,
        IEnumerable<Invoice> earlierInvoices, IEnumerable<Receipt> receipts)
    {
        if (periodFrom > closingDate)
        {
            throw new ArgumentException($"期間の開始 {periodFrom:yyyy-MM-dd} が締め日 {closingDate:yyyy-MM-dd} より後。", nameof(periodFrom));
        }
        // 請求書は請求先 × 締め日で作る(業務ルール集「4. 請求」)
        if (billingCustomer.BillingTarget is var target && target != billingCustomer)
        {
            throw new InvalidInputException($"{billingCustomer.Code} の請求先は {target.Code}。請求書は請求先で締める。", nameof(billingCustomer));
        }
        var records = salesRecords.ToList();
        if (records.Count == 0)
        {
            throw new BusinessRuleException($"{closingDate:yyyy-MM-dd} 締めで請求する売上が無い。");
        }
        if (records.FirstOrDefault(r => r.IsBilled) is { } billed)
        {
            throw new BusinessRuleException($"売上 {billed.Id} は締め済み(請求書 {billed.Invoice?.Id ?? billed.InvoiceId})。二重に請求しない。");
        }
        // 締める売上を集める条件(ユースケースの検索)を誤っても、別の請求先の売上は入れない
        if (records.FirstOrDefault(r => BillingCustomerOf(r) != billingCustomer) is { } others)
        {
            throw new BusinessRuleException($"売上 {others.Id} の請求先は {others.BillingCustomer.Code}。{billingCustomer.Code} の請求書に入れない。");
        }
        if (records.FirstOrDefault(r => r.RecordedOn > closingDate) is { } later)
        {
            throw new BusinessRuleException($"売上日 {later.RecordedOn:yyyy-MM-dd} の売上は、{closingDate:yyyy-MM-dd} 締めに入れられない。");
        }

        var earlier = earlierInvoices.ToList();
        var paid = receipts.ToList();
        // 前の請求書・入金を集める条件(ユースケースの検索)を誤っても、別の請求先の分や、今回以後の請求書を繰越に入れない
        if (earlier.FirstOrDefault(i => i.BillingCustomer is null) is { } unloadedInvoice)
        {
            throw new InvalidOperationException($"請求書 {unloadedInvoice.Id} の請求先を読み込んでいない。");
        }
        if (paid.FirstOrDefault(r => r.BillingCustomer is null) is { } unloadedReceipt)
        {
            throw new InvalidOperationException($"入金 {unloadedReceipt.Id} の請求先を読み込んでいない。");
        }
        if (earlier.FirstOrDefault(i => i.BillingCustomer != billingCustomer) is { } othersInvoice)
        {
            throw new BusinessRuleException($"請求書 {othersInvoice.Id} の請求先は {othersInvoice.BillingCustomer.Code}。{billingCustomer.Code} の繰越に入れない。");
        }
        if (paid.FirstOrDefault(r => r.BillingCustomer != billingCustomer) is { } othersReceipt)
        {
            throw new BusinessRuleException($"入金 {othersReceipt.Id} の請求先は {othersReceipt.BillingCustomer.Code}。{billingCustomer.Code} の繰越に入れない。");
        }
        if (earlier.FirstOrDefault(i => i.ClosingDate >= closingDate) is { } notEarlier)
        {
            throw new BusinessRuleException($"{notEarlier.ClosingDate:yyyy-MM-dd} 締めの請求書がある。{closingDate:yyyy-MM-dd} では締められない。");
        }
        var previousBalance = earlier.MaxBy(i => i.ClosingDate)?.TotalAmount ?? Money.Zero;
        // まだどの請求書にも数えていない入金(締めた後に過去の日付で登録した入金も、ここで数える。2026-11-25 本人の決定)
        var receivedAmount = paid.Where(r => r.ReceivedOn <= closingDate).Aggregate(Money.Zero, (sum, r) => sum + r.Amount)
            - earlier.Aggregate(Money.Zero, (sum, i) => sum + i.ReceivedAmount);

        var invoice = new Invoice(billingCustomer, periodFrom, closingDate, records, previousBalance, receivedAmount);
        // 今回分が0円以下(返品だけ、出荷と全数返品など)なら締めない。売上は未請求のまま次の締めに回し、次の請求から差し引く
        // (2026-10-16 決定)。今回分は入金を充てる上限で、DB の検査制約が0以上を求める。前受で請求額が0円以下になるだけなら締める(2026-11-25 本人の決定)
        if (invoice.ChargedAmount.Yen <= 0m)
        {
            throw new BusinessRuleException(
                $"{closingDate:yyyy-MM-dd} 締めの今回分が {invoice.ChargedAmount.Yen} 円で、0円以下なので締めない。売上は次の締めに回す。");
        }
        foreach (var record in records)
        {
            record.BillTo(invoice);
        }
        return invoice;
    }

    /// <summary>
    /// 入金を充てる(負なら打ち消す)。額は <see cref="Receipt"/> が未回収の範囲で決める。
    /// 0以上・今回分以下は DB の検査制約 ck_invoices_allocated が最後に止める。
    /// </summary>
    internal void Allocate(Money amount) => AllocatedAmount += amount;

    // 同じ DbContext で請求先と売上を読めば、同じ得意先は同じオブジェクトになる。読み込んでいなければ、別の請求先と取り違えずに止める
    private static Customer BillingCustomerOf(SalesRecord record) =>
        record.BillingCustomer ?? throw new InvalidOperationException($"売上 {record.Id} の請求先を読み込んでいない。");
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
