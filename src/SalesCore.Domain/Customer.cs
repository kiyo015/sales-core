namespace SalesCore.Domain;

/// <summary>
/// 得意先。出荷先であり、請求先にもなる。支店に出荷して本社に請求するときは、支店の請求先に本社を指定する。
/// 締め日は請求先のときだけ意味を持つ(docs/domain/business-rules.md の「4. 請求」)。
/// </summary>
public sealed class Customer
{
    /// <summary>選べる締め日。31 は月末。</summary>
    public static readonly IReadOnlySet<int> ClosingDays = new HashSet<int> { 5, 10, 15, 20, 25, 31 };

    private Customer()
    {
        // DB から読み込むとき(EF Core)に使う
    }

    public Customer(string code, string name, int closingDay, Customer? billingCustomer = null)
    {
        Code = MasterInput.Code(code, "得意先コード", nameof(code));
        Name = MasterInput.Text(name, "得意先名", 100, nameof(name));
        if (!ClosingDays.Contains(closingDay))
        {
            throw new InvalidInputException($"締め日は 5・10・15・20・25・31(月末)から選ぶ({closingDay})。", nameof(closingDay));
        }
        // 請求書は請求先(請求先を持たない得意先)で締めるので、請求先は1段まで(本人の決定 2026-10-30)。
        // 請求先の BillingCustomer を読み込まずに渡すと、BillingTarget が止める(黙って通さない)。
        // 読み込み忘れはプログラムの誤りなので、入力の誤り(400)にせず InvalidOperationException(500)のままにする(PR #17 の AIレビュー)
        if (billingCustomer is not null && billingCustomer.BillingTarget is var upper && upper != billingCustomer)
        {
            throw new InvalidInputException(
                $"{billingCustomer.Code} は請求先({upper.Code})を持つので、請求先にできない。{upper.Code} を指定する。", nameof(billingCustomer));
        }
        ClosingDay = closingDay;
        BillingCustomer = billingCustomer;
    }

    public long Id { get; private set; }

    public string Code { get; private set; } = "";

    public string Name { get; private set; } = "";

    /// <summary>締め日。31 は月末。</summary>
    public int ClosingDay { get; private set; }

    /// <summary>請求先にする別の得意先。無ければ自分が請求先。</summary>
    public Customer? BillingCustomer { get; private set; }

    /// <summary>
    /// 請求先の id。DB から読むときは請求先を読み込まなくても入っているので、請求先を持つかどうかはこれでも分かる
    /// (請求先だけを見ると、読み込んでいない支店が自分を請求先にしているように見える)。
    /// </summary>
    public long? BillingCustomerId { get; private set; }

    /// <summary>請求書を送る相手。DB から読んだ得意先は、請求先を読み込んでいないと止まる(自分を請求先と取り違えない)。</summary>
    public Customer BillingTarget => BillingCustomer
        ?? (BillingCustomerId is null ? this : throw new InvalidOperationException($"得意先 {Code} の請求先を読み込んでいない。"));

    public bool IsActive { get; private set; } = true;

    /// <summary>その月の締め日。31 は月末(2月なら28日か29日)。</summary>
    public DateOnly ClosingDateIn(int year, int month) =>
        new(year, month, Math.Min(ClosingDay, DateTime.DaysInMonth(year, month)));

    public bool IsClosingDate(DateOnly date) => date == ClosingDateIn(date.Year, date.Month);

    /// <summary>今日以前で最も近い締め日。締めの画面の初期値(締め日が来るまでは締められないので、今日より後にはしない)。</summary>
    public DateOnly LatestClosingDate(DateOnly today)
    {
        var thisMonth = ClosingDateIn(today.Year, today.Month);
        return thisMonth <= today ? thisMonth : PreviousClosingDate(thisMonth);
    }

    /// <summary>前回の締め日(前の月の締め日)。今回の請求の期間は、その翌日から。</summary>
    public DateOnly PreviousClosingDate(DateOnly closingDate)
    {
        var previousMonth = closingDate.AddMonths(-1);
        return ClosingDateIn(previousMonth.Year, previousMonth.Month);
    }
}
