namespace SalesCore.Domain;

/// <summary>見積の状態。作成中 → 提出済 → 受注済 ／ 失注。逆戻りしない。</summary>
public enum QuotationStatus
{
    /// <summary>作成中</summary>
    Draft,

    /// <summary>提出済(得意先に出した)</summary>
    Submitted,

    /// <summary>受注済</summary>
    Ordered,

    /// <summary>失注</summary>
    Lost,
}

/// <summary>状態ごとにできる操作(業務ルール集「見積と得意先別単価」2026-11-17 本人の決定)。画面がボタンを出すかもこれを見る。</summary>
public static class QuotationStatusRules
{
    /// <summary>明細を直せるのは作成中だけ。提出済を直す時は新しい見積を作る(得意先に出した内容は変えない)。</summary>
    public static bool CanChange(this QuotationStatus status) => status is QuotationStatus.Draft;

    public static bool CanSubmit(this QuotationStatus status) => status is QuotationStatus.Draft;

    /// <summary>失注にできるのは作成中と提出済(出さずにやめた見積も、作成中のまま残さない)。</summary>
    public static bool CanMarkLost(this QuotationStatus status) => status is QuotationStatus.Draft or QuotationStatus.Submitted;

    /// <summary>受注にできるのは提出済だけ。</summary>
    public static bool CanOrder(this QuotationStatus status) => status is QuotationStatus.Submitted;
}

/// <summary>
/// 見積。金額と税は受注と同じ計算(明細金額は四捨五入、税は税率ごとに1回)。
/// 単価・税率は見積日で決めて明細に写す(決めるのはユースケース)。物理削除せず、やめた見積は失注にする。
/// </summary>
public sealed class Quotation
{
    private readonly List<QuotationLine> _lines = [];

    private Quotation()
    {
        // DB から読み込むとき(EF Core)に使う
    }

    public Quotation(Customer customer, DateOnly quotedOn, DateOnly validUntil, IEnumerable<QuotationLine> lines)
    {
        _lines = lines.ToList();
        if (_lines.Count == 0)
        {
            throw new InvalidInputException("見積には明細が1行以上要る。", nameof(lines));
        }
        if (validUntil < quotedOn)
        {
            throw new InvalidInputException($"有効期限 {validUntil:yyyy-MM-dd} は見積日 {quotedOn:yyyy-MM-dd} より前にできない。", nameof(validUntil));
        }
        Customer = customer;
        QuotedOn = quotedOn;
        ValidUntil = validUntil;
    }

    public long Id { get; private set; }

    public IReadOnlyList<QuotationLine> Lines => _lines;

    /// <summary>見積先の得意先。受注にすると、この得意先の受注になる。</summary>
    public Customer Customer { get; private set; } = null!;

    /// <summary>見積日。単価と税率はこの日で決める。</summary>
    public DateOnly QuotedOn { get; private set; }

    /// <summary>有効期限。この日(日本時間)までは受注にでき、翌日からできない。</summary>
    public DateOnly ValidUntil { get; private set; }

    public QuotationStatus Status { get; private set; } = QuotationStatus.Draft;

    /// <summary>税抜合計(明細金額の合計)。</summary>
    public Money Amount => _lines.Aggregate(Money.Zero, (total, line) => total + line.Amount);

    /// <summary>税率ごとの税額(税率ごとに1回だけ四捨五入)。</summary>
    public TaxBreakdown Tax => TaxCalculator.Calculate(_lines.Select(line => new TaxableLine(line.Amount, line.TaxRate)));

    /// <summary>
    /// 明細の数量と単価を直す。作成中だけ。明細を消す操作は作らない(物理削除しない。要らない明細があれば、新しい見積を作り、前の見積は失注にする)。
    /// </summary>
    public void ChangeLine(QuotationLine line, decimal quantity, decimal unitPrice)
    {
        Require(Status.CanChange(), "直すことが");
        if (!_lines.Contains(line))
        {
            throw new InvalidInputException("この見積の明細ではない。", nameof(line));
        }
        line.Change(quantity, unitPrice);
    }

    public void Submit()
    {
        Require(Status.CanSubmit(), "提出");
        Status = QuotationStatus.Submitted;
    }

    public void MarkLost()
    {
        Require(Status.CanMarkLost(), "失注に");
        Status = QuotationStatus.Lost;
    }

    /// <summary>受注済にする。<paramref name="today"/> は日本時間の今日(呼ぶ側がサーバーの時計で決める)。有効期限の当日まで。</summary>
    public void MarkOrdered(DateOnly today)
    {
        Require(Status.CanOrder(), "受注に");
        if (today > ValidUntil)
        {
            throw new BusinessRuleException($"有効期限 {ValidUntil:yyyy-MM-dd} を過ぎた見積は受注にできない(今日 {today:yyyy-MM-dd})。");
        }
        Status = QuotationStatus.Ordered;
    }

    private void Require(bool allowed, string operation)
    {
        if (!allowed)
        {
            throw new BusinessRuleException($"状態が {Status} の見積は{operation}できない。");
        }
    }
}

/// <summary>見積明細。数量・単価の決まりは受注明細と同じ(<see cref="SalesOrderLine.EnsureLine"/>)。</summary>
public sealed class QuotationLine
{
    private QuotationLine()
    {
        // DB から読み込むとき(EF Core)に使う
    }

    public QuotationLine(decimal quantity, decimal unitPrice, TaxRate taxRate)
    {
        SalesOrderLine.EnsureLine(quantity, unitPrice);
        Quantity = quantity;
        UnitPrice = unitPrice;
        TaxRate = taxRate;
    }

    public long Id { get; private set; }

    public decimal Quantity { get; private set; }

    /// <summary>見積日の単価(明細で指定 → 見積日の得意先別単価 → 標準単価)。</summary>
    public decimal UnitPrice { get; private set; }

    /// <summary>見積日の税率。受注にする時は受注日の税率で決め直す(2026-11-17 本人の決定)。</summary>
    public TaxRate TaxRate { get; private set; }

    /// <summary>明細金額(税抜)。単価 × 数量を四捨五入。</summary>
    public Money Amount => Money.Round(UnitPrice * Quantity);

    internal void Change(decimal quantity, decimal unitPrice)
    {
        SalesOrderLine.EnsureLine(quantity, unitPrice);
        Quantity = quantity;
        UnitPrice = unitPrice;
    }
}
