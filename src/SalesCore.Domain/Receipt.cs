namespace SalesCore.Domain;

/// <summary>
/// 入金。登録すると、請求先の未回収の請求書に締め日の古い順に充てる(消込)。充て切れない分は前受として残る。
/// 取消は赤黒(打ち消しの入金と割当)で表し、元の入金は書き換えない。
/// 決まりは docs/domain/business-rules.md の「4. 請求」の「入金と消込」(2026-11-12・2026-11-20 本人の決定)。
/// </summary>
public sealed class Receipt
{
    // 金額の列は numeric(15,0)。超えると DB の桁あふれになるので、その前に入力の誤りとして止める
    private static readonly Money MaxAmount = Money.Of(999_999_999_999_999m);

    private readonly List<ReceiptAllocation> _allocations = [];

    private Receipt()
    {
        // DB から読み込むとき(EF Core)に使う
    }

    private Receipt(Customer billingCustomer, DateOnly receivedOn, Money amount, Receipt? reverses)
    {
        BillingCustomer = billingCustomer;
        ReceivedOn = receivedOn;
        Amount = amount;
        Reverses = reverses;
    }

    public long Id { get; private set; }

    /// <summary>入金元の請求先(請求先を持たない得意先)。</summary>
    public Customer BillingCustomer { get; private set; } = null!;

    public DateOnly ReceivedOn { get; private set; }

    /// <summary>入金額。打ち消しの入金は負。</summary>
    public Money Amount { get; private set; }

    /// <summary>
    /// 割当の合計。割当を読み込まなくても分かるように、入金の行に持つ(DB の検査制約 ck_receipts_allocated で、
    /// 入金額と同じ符号・入金額以下に止める)。
    /// </summary>
    public Money AllocatedAmount { get; private set; }

    /// <summary>前受(どの請求書にも充てていない残り)。</summary>
    public Money Unallocated => Amount - AllocatedAmount;

    /// <summary>打ち消しの入金なら、元の入金。</summary>
    public Receipt? Reverses { get; private set; }

    public long? ReversesId { get; private set; }

    /// <summary>打ち消された元の入金。</summary>
    public bool IsCancelled { get; private set; }

    /// <summary>
    /// 繰越の入金額を数える日。打ち消しの入金は、元の入金の入金日で数える(締め日の後・締めの前に取り消した入金を、
    /// その請求書に入金済みと出さない。2026-11-25 本人の決定)。打ち消しの入金の日付(<see cref="ReceivedOn"/>)は取り消した日のまま。
    /// </summary>
    public DateOnly CountedOn => Reverses?.ReceivedOn
        ?? (ReversesId is null ? ReceivedOn : throw new InvalidOperationException($"入金 {Id} の元の入金を読み込んでいない。"));

    public IReadOnlyList<ReceiptAllocation> Allocations => _allocations;

    /// <summary>
    /// 入金を登録し、渡した請求書のうち未回収のものに、締め日の古い順に充てる。
    /// 請求書の上限は今回分(今回売上＋消費税)で、繰越は充てる対象にしない(2026-11-20 本人の決定)。
    /// </summary>
    /// <param name="invoices">請求先の請求書。消し終えたものが混じっていてもよい(飛ばす)。順は問わない。</param>
    /// <param name="today">日本時間の今日。入金日はこの日まで(2026-11-20 本人の決定)。</param>
    public static Receipt Register(Customer billingCustomer, DateOnly receivedOn, Money amount, IEnumerable<Invoice> invoices, DateOnly today)
    {
        if (billingCustomer.BillingTarget is var target && target != billingCustomer)
        {
            throw new InvalidInputException($"{billingCustomer.Code} の請求先は {target.Code}。入金は請求先で受ける。", nameof(billingCustomer));
        }
        if (amount.Yen <= 0m)
        {
            throw new InvalidInputException($"入金額 {amount.Yen} 円は、1円以上にする。", nameof(amount));
        }
        if (amount.Yen > MaxAmount.Yen)
        {
            throw new InvalidInputException($"入金額は {MaxAmount.Yen:#,0} 円まで。", nameof(amount));
        }
        if (receivedOn > today)
        {
            throw new InvalidInputException($"入金日 {receivedOn:yyyy-MM-dd} は、今日({today:yyyy-MM-dd})より先にできない。", nameof(receivedOn));
        }
        var targets = invoices.ToList();
        // 請求先を読み込んでいない請求書は、比べると「別の請求先」に見える。誤りの理由を取り違えないよう先に止める(Invoice.BillingCustomerOf と同じ)
        if (targets.FirstOrDefault(i => i.BillingCustomer is null) is { } unloaded)
        {
            throw new InvalidOperationException($"請求書 {unloaded.Id} の請求先を読み込んでいない。");
        }
        // 未回収の請求書を集める条件(ユースケースの検索)を誤っても、別の請求先の請求書には充てない
        if (targets.FirstOrDefault(i => i.BillingCustomer != billingCustomer) is { } others)
        {
            throw new BusinessRuleException($"請求書 {others.Id} の請求先は {others.BillingCustomer.Code}。{billingCustomer.Code} の入金を充てない。");
        }

        var receipt = new Receipt(billingCustomer, receivedOn, amount, reverses: null);
        foreach (var invoice in targets.Where(i => i.Outstanding.Yen > 0m).OrderBy(i => i.ClosingDate).ThenBy(i => i.Id))
        {
            var rest = receipt.Unallocated;
            if (rest.Yen == 0m)
            {
                break;
            }
            receipt.AllocateTo(invoice, rest.Yen < invoice.Outstanding.Yen ? rest : invoice.Outstanding);
        }
        return receipt;
    }

    /// <summary>
    /// 取り消す。打ち消しの入金(今日の日付・負の額)を作り、元の割当を同じ請求書・同じ額で打ち消す。
    /// 割当(と請求書)を読み込んでから呼ぶ。読み込んでいないと、請求書を未回収に戻せないので止める。
    /// </summary>
    /// <param name="today">日本時間の今日。打ち消しの入金の日付(2026-11-20 本人の決定)。</param>
    public Receipt Cancel(DateOnly today)
    {
        if (Reverses is not null || ReversesId is not null)
        {
            throw new BusinessRuleException("打ち消しの入金は取り消せない。入れ直すなら、新しい入金を登録する。");
        }
        if (IsCancelled)
        {
            throw new BusinessRuleException($"入金 {Id} は取り消し済み。");
        }
        // 読み込み忘れは、どの請求書も書き換える前に止める(途中まで戻してから落ちない。PR #28 の AIレビュー)
        if (_allocations.Aggregate(Money.Zero, (sum, a) => sum + a.Amount) != AllocatedAmount)
        {
            throw new InvalidOperationException($"入金 {Id} の割当を読み込んでいない。");
        }
        if (_allocations.Any(a => a.Invoice is null))
        {
            throw new InvalidOperationException($"入金 {Id} の割当の請求書を読み込んでいない。");
        }
        if (BillingCustomer is null)
        {
            throw new InvalidOperationException($"入金 {Id} の請求先を読み込んでいない。");
        }

        var reversal = new Receipt(BillingCustomer, today, -Amount, reverses: this);
        foreach (var allocation in _allocations)
        {
            reversal.AllocateTo(allocation.Invoice, -allocation.Amount);
        }
        IsCancelled = true;
        return reversal;
    }

    /// <summary>
    /// 締めた時に、前受を未回収の請求書に充てる(2026-11-20 本人の決定)。入金は入金日の古い順(同じ日なら登録の順)、
    /// 請求書は締め日の古い順(登録の時と同じ)。取り消した入金と打ち消しの入金は充てない(取り消した前受は、打ち消しの負の前受と足して0)。
    /// </summary>
    /// <param name="receipts">請求先の入金(取り消したもの・打ち消しが混じっていてもよい)。</param>
    /// <param name="invoices">請求先の請求書(締めた請求書を含む)。</param>
    public static void AllocateAdvances(IEnumerable<Receipt> receipts, IEnumerable<Invoice> invoices)
    {
        var advances = receipts.ToList();
        var targets = invoices.ToList();
        if (targets.FirstOrDefault(i => i.BillingCustomer is null) is { } unloadedInvoice)
        {
            throw new InvalidOperationException($"請求書 {unloadedInvoice.Id} の請求先を読み込んでいない。");
        }
        if (advances.FirstOrDefault(r => r.BillingCustomer is null) is { } unloadedReceipt)
        {
            throw new InvalidOperationException($"入金 {unloadedReceipt.Id} の請求先を読み込んでいない。");
        }
        // 入金と請求書を集める条件(ユースケースの検索)を誤っても、別の請求先には充てない
        var customers = targets.Select(i => i.BillingCustomer).Concat(advances.Select(r => r.BillingCustomer)).Distinct().ToList();
        if (customers.Count > 1)
        {
            throw new BusinessRuleException($"請求先 {string.Join("・", customers.Select(c => c.Code))} の入金・請求書を、1つの前受の充当に混ぜない。");
        }

        var open = targets.Where(i => i.Outstanding.Yen > 0m).OrderBy(i => i.ClosingDate).ThenBy(i => i.Id).ToList();
        // 打ち消しの入金は負の額なので、前受(残り)は0以下で、ここで外れる(除く条件を別に書いても届かない。2026-11-25 の対照実験 V19)
        foreach (var receipt in advances
                     .Where(r => !r.IsCancelled && r.Unallocated.Yen > 0m)
                     .OrderBy(r => r.ReceivedOn).ThenBy(r => r.Id))
        {
            foreach (var invoice in open.Where(i => i.Outstanding.Yen > 0m))
            {
                var rest = receipt.Unallocated;
                if (rest.Yen == 0m)
                {
                    break;
                }
                receipt.AllocateTo(invoice, rest.Yen < invoice.Outstanding.Yen ? rest : invoice.Outstanding);
            }
        }
    }

    private void AllocateTo(Invoice invoice, Money amount)
    {
        invoice.Allocate(amount);
        _allocations.Add(new ReceiptAllocation(invoice, amount));
        AllocatedAmount += amount;
    }
}

/// <summary>入金の割当(receipt_allocations の1行)。どの請求書に、いくら充てたか。打ち消しは負。</summary>
public sealed class ReceiptAllocation
{
    private ReceiptAllocation()
    {
        // DB から読み込むとき(EF Core)に使う
    }

    internal ReceiptAllocation(Invoice invoice, Money amount)
    {
        Invoice = invoice;
        Amount = amount;
    }

    public long Id { get; private set; }

    public Invoice Invoice { get; private set; } = null!;

    public Money Amount { get; private set; }
}
