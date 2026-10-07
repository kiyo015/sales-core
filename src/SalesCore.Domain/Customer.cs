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
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("得意先コードは空にできない。", nameof(code));
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("得意先名は空にできない。", nameof(name));
        }
        if (!ClosingDays.Contains(closingDay))
        {
            throw new ArgumentException($"締め日は 5・10・15・20・25・31(月末)から選ぶ({closingDay})。", nameof(closingDay));
        }
        Code = code;
        Name = name;
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

    /// <summary>請求書を送る相手。</summary>
    public Customer BillingTarget => BillingCustomer ?? this;

    public bool IsActive { get; private set; } = true;
}
