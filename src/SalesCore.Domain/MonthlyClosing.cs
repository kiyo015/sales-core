namespace SalesCore.Domain;

/// <summary>
/// 月次締め(年月ごと)。締めた時に、請求先 × 年月の売掛金の残高(期首 ＋ 売上 ＋ 消費税 − 入金 ＝ 期末)を確定する。
/// 締めた月の末日以前の日付では、出荷の確定・返品・入金・請求の締めをしない(<see cref="EnsureOpen"/>。業務ルール集「4. 請求」の「月次締め」)。
/// </summary>
public sealed class MonthlyClosing
{
    private readonly List<ReceivableBalance> _balances = [];

    private MonthlyClosing()
    {
        // DB から読み込むとき(EF Core)に使う
    }

    private MonthlyClosing(DateOnly month, List<ReceivableBalance> balances)
    {
        Month = month;
        _balances = balances;
    }

    public long Id { get; private set; }

    /// <summary>締めた月(1日)。</summary>
    public DateOnly Month { get; private set; }

    /// <summary>締めた月の末日。この日以前の日付は入れられない。</summary>
    public DateOnly LastDay => Month.AddMonths(1).AddDays(-1);

    /// <summary>請求先ごとの残高。期首も月の額も全部0の請求先は行を作らない。請求先のコードの順。</summary>
    public IReadOnlyList<ReceivableBalance> Balances => _balances;

    /// <summary>
    /// 月を締めて残高を確定する。売上は売上日の月に税抜で、消費税は請求書の締め日の月に、入金は入金日の月に入れる
    /// (2026-11-26 本人の決定。消費税は請求書ごとに決まるので、未請求の売上は税抜のまま残高に入る)。打ち消しの入金は取り消した日の月で引く。
    /// </summary>
    /// <param name="previous">前の月の月次締め(残高を読み込んだもの)。最初の締めは null。</param>
    /// <param name="firstTransactionDate">売上・入金の一番古い日付。最初の締めだけで使う(その月から締める)。</param>
    /// <param name="salesRecords">その月の売上と、月末までの未請求の売上(締め忘れの検査に使う)。</param>
    /// <param name="invoices">請求書(締め日がその月のものの税を入れる。締め忘れの検査にはその月以後の締め日のものも使う)。</param>
    /// <param name="receipts">その月の入金(打ち消しの入金も)。</param>
    public static MonthlyClosing Close(int year, int month, DateOnly today, MonthlyClosing? previous, DateOnly? firstTransactionDate,
        IEnumerable<SalesRecord> salesRecords, IEnumerable<Invoice> invoices, IEnumerable<Receipt> receipts)
    {
        if (year is < 1 or > 9998 || month is < 1 or > 12)
        {
            throw new InvalidInputException($"{year}年{month}月は締められる年月ではない。");
        }
        var closing = new MonthlyClosing(new DateOnly(year, month, 1), []);
        var first = closing.Month;
        var last = closing.LastDay;
        if (last >= today)
        {
            throw new InvalidInputException($"{Name(first)}はまだ終わっていない(今日は {today:yyyy-MM-dd})。月が終わってから締める。");
        }
        if (previous is not null)
        {
            if (previous.Month >= first) throw new BusinessRuleException($"{Name(previous.Month)}まで締め済み。{Name(first)}は締められない。");
            if (previous.Month.AddMonths(1) != first) throw new BusinessRuleException($"{Name(previous.Month.AddMonths(1))}を先に締める。");
        }
        else
        {
            // 最初の締めは取引の一番古い月から(2026-11-26 本人の決定)。期首は0
            var firstMonth = firstTransactionDate ?? throw new BusinessRuleException("締める取引(売上・入金)が無い。");
            if (firstMonth.AddDays(1 - firstMonth.Day) != first)
            {
                throw new BusinessRuleException($"最初の月次締めは、取引の一番古い月({Name(firstMonth)})から締める。");
            }
        }

        var sales = salesRecords.ToList();
        var billed = invoices.ToList();
        var paid = receipts.ToList();
        EnsureBilled(year, month, sales, billed);

        bool InMonth(DateOnly date) => date >= first && date <= last;
        var opening = (previous?.Balances ?? []).ToDictionary(b => Loaded(b.BillingCustomer, "残高", b.Id), b => b.Closing);
        var monthSales = sales.Where(r => InMonth(r.RecordedOn)).ToLookup(r => Loaded(r.BillingCustomer, "売上", r.Id));
        var monthTax = billed.Where(i => InMonth(i.ClosingDate)).ToLookup(i => Loaded(i.BillingCustomer, "請求書", i.Id));
        var monthReceipts = paid.Where(r => InMonth(r.ReceivedOn)).ToLookup(r => Loaded(r.BillingCustomer, "入金", r.Id));

        foreach (var customer in opening.Keys.Concat(monthSales.Select(g => g.Key)).Concat(monthTax.Select(g => g.Key))
                     .Concat(monthReceipts.Select(g => g.Key)).Distinct().OrderBy(c => c.Code, StringComparer.Ordinal))
        {
            var balance = new ReceivableBalance(customer, opening.GetValueOrDefault(customer),
                Sum(monthSales[customer], r => r.Amount), Sum(monthTax[customer], i => i.TaxAmount), Sum(monthReceipts[customer], r => r.Amount));
            if (balance.Opening.Yen != 0m || balance.Sales.Yen != 0m || balance.Tax.Yen != 0m || balance.Received.Yen != 0m)
            {
                closing._balances.Add(balance);
            }
        }
        return closing;
    }

    /// <summary>
    /// 日付が月次締めした月(末日まで)に入っていれば止める。出荷の確定・返品・入金・請求の締めの前に呼ぶ。
    /// 締めた月の誤りは、翌月の日付で赤黒にして直す(業務ルール集「締めた後に誤りが見つかったら」)。
    /// </summary>
    /// <param name="latest">一番新しい月次締め。まだ締めていなければ null。</param>
    public static void EnsureOpen(MonthlyClosing? latest, DateOnly date, string what)
    {
        if (latest is not null && date <= latest.LastDay)
        {
            throw new BusinessRuleException($"{what} {date:yyyy-MM-dd} は、月次締めした{Name(latest.Month)}以前の日付。締めた月の誤りは翌月の日付で赤黒にして直す。");
        }
    }

    // その月の締め日の請求書を締めていない請求先があれば止める(2026-11-26 本人の決定)。消費税が翌月にずれないように
    private static void EnsureBilled(int year, int month, List<SalesRecord> sales, List<Invoice> invoices)
    {
        var pending = sales.Where(r => !r.IsBilled)
            .GroupBy(r => Loaded(r.BillingCustomer, "売上", r.Id))
            .Select(g => (Customer: g.Key, ClosingDate: g.Key.ClosingDateIn(year, month), Records: g.ToList()))
            .Where(p => p.Records.Any(r => r.RecordedOn <= p.ClosingDate)
                // 締め日以後の請求書があれば、後から入れた締め日以前の売上は次の請求書に入る(請求の締めの後・月次締めの前は入れてよい)
                && !invoices.Any(i => Loaded(i.BillingCustomer, "請求書", i.Id) == p.Customer && i.ClosingDate >= p.ClosingDate)
                // 今回分が0円以下なら請求書を作らずに次へ回す(Invoice.Create と同じ)ので、締め忘れではない
                && TaxCalculator.Calculate(p.Records.Where(r => r.RecordedOn <= p.ClosingDate).SelectMany(r => r.Lines)
                    .Select(l => new TaxableLine(l.Amount, l.TaxRate))).TotalWithTax.Yen > 0m)
            .OrderBy(p => p.Customer.Code, StringComparer.Ordinal)
            .Select(p => $"{p.Customer.Code}({p.ClosingDate:yyyy-MM-dd} 締め)")
            .ToList();
        if (pending.Count > 0)
        {
            throw new BusinessRuleException($"請求の締めが済んでいない請求先がある: {string.Join("、", pending)}。請求書を締めてから月次締めする。");
        }
    }

    private static string Name(DateOnly month) => $"{month.Year}年{month.Month}月";

    private static Money Sum<T>(IEnumerable<T> items, Func<T, Money> amount) => items.Aggregate(Money.Zero, (total, item) => total + amount(item));

    // 同じ DbContext で読めば、同じ請求先は同じオブジェクトになる。読み込んでいなければ、別の請求先と取り違えずに止める
    private static Customer Loaded(Customer? customer, string what, long id) =>
        customer ?? throw new InvalidOperationException($"{what} {id} の請求先を読み込んでいない。");
}

/// <summary>売掛金の残高(receivable_balances の1行)。請求先 × 年月。期末 ＝ 期首 ＋ 売上 ＋ 消費税 − 入金(DB の検査制約でも止める)。</summary>
public sealed class ReceivableBalance
{
    private ReceivableBalance()
    {
        // DB から読み込むとき(EF Core)に使う
    }

    internal ReceivableBalance(Customer billingCustomer, Money opening, Money sales, Money tax, Money received)
    {
        BillingCustomer = billingCustomer;
        Opening = opening;
        Sales = sales;
        Tax = tax;
        Received = received;
        Closing = opening + sales + tax - received;
    }

    public long Id { get; private set; }

    public Customer BillingCustomer { get; private set; } = null!;

    /// <summary>期首(前の月の期末。最初の月は0)。</summary>
    public Money Opening { get; private set; }

    /// <summary>売上(税抜。売上日がその月。返品は負)。</summary>
    public Money Sales { get; private set; }

    /// <summary>消費税(締め日がその月の請求書の税)。</summary>
    public Money Tax { get; private set; }

    /// <summary>入金(入金日がその月。打ち消しの入金は負)。</summary>
    public Money Received { get; private set; }

    /// <summary>期末。負なら前受。</summary>
    public Money Closing { get; private set; }
}
