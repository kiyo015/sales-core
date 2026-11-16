namespace SalesCore.Domain;

/// <summary>
/// 得意先別単価(得意先 × 商品 × 期間)。業務ルール集「3. 販売の流れ」の「見積と得意先別単価」(2026-11-13・11-16 本人の決定)。
/// 期間は初日と末日を含み、末日が空なら無期限。請求先にだけ登録でき、支店の受注は請求先の単価を使う。
/// 改定は新しい単価の登録で行い、無期限の前の単価を新しい初日の前日で閉じる。誤りは無効にする(物理削除しない)。
/// 決めた単価は受注明細に写すので、後で単価を改定・無効にしても、過去の受注は変わらない。
/// </summary>
public sealed class CustomerPrice
{
    private CustomerPrice()
    {
        // DB から読み込むとき(EF Core)に使う
    }

    private CustomerPrice(Customer customer, Product product, decimal unitPrice, DateOnly validFrom, DateOnly? validTo)
    {
        Customer = customer;
        Product = product;
        UnitPrice = unitPrice;
        ValidFrom = validFrom;
        ValidTo = validTo;
    }

    public long Id { get; private set; }

    public Customer Customer { get; private set; } = null!;

    public Product Product { get; private set; } = null!;

    public decimal UnitPrice { get; private set; }

    public DateOnly ValidFrom { get; private set; }

    /// <summary>この日まで有効。無ければ無期限。</summary>
    public DateOnly? ValidTo { get; private set; }

    public bool IsActive { get; private set; } = true;

    /// <summary>
    /// 単価を登録する。<paramref name="existing"/> には、同じ得意先・商品の登録済みの単価を渡す(ほかの得意先・商品のものは見ない)。
    /// 無期限の前の単価で、初日が新しい初日より前のものは、新しい初日の前日で閉じる。ほかの重なりは拒む(前の単価は変えない)。
    /// </summary>
    public static CustomerPrice Register(
        Customer customer, Product product, decimal unitPrice, DateOnly validFrom, DateOnly? validTo, IEnumerable<CustomerPrice> existing)
    {
        if (customer.BillingTarget is var billing && billing != customer)
        {
            throw new InvalidInputException(
                $"{customer.Code} は請求先({billing.Code})を持つ。受注は請求先の単価を使うので、{billing.Code} に登録する。", nameof(customer));
        }
        MasterInput.UnitPrice(unitPrice, "単価", nameof(unitPrice));
        if (validTo < validFrom)
        {
            throw new InvalidInputException($"末日({validTo})は初日({validFrom})より前にできない。", nameof(validTo));
        }

        var same = existing.Where(p => p.IsActive && p.Customer == customer && p.Product == product).ToList();
        var closing = same.SingleOrDefault(p => p.ValidTo is null && p.ValidFrom < validFrom);
        var dayBefore = validFrom.AddDays(-1);
        // 閉じた後の期間で重なりを見る。拒む時は、前の単価を閉じる前に止める
        if (same.FirstOrDefault(p => Overlaps(p.ValidFrom, p == closing ? dayBefore : p.ValidTo, validFrom, validTo)) is { } clash)
        {
            throw new BusinessRuleException(
                $"{customer.Code} の {product.Code} の単価は、{clash.ValidFrom:yyyy-MM-dd}〜{clash.ValidTo?.ToString("yyyy-MM-dd") ?? "無期限"} の単価と期間が重なる。");
        }
        if (closing is not null) closing.ValidTo = dayBefore;
        return new CustomerPrice(customer, product, unitPrice, validFrom, validTo);
    }

    /// <summary>誤って登録した単価を無効にする。無効の単価は受注で引かず、重なりの判定からも外す。</summary>
    public void Invalidate()
    {
        if (!IsActive) throw new BusinessRuleException($"{ValidFrom:yyyy-MM-dd} からの単価は既に無効。");
        IsActive = false;
    }

    /// <summary>
    /// 受注の単価。明細で指定 → 受注日に有効な請求先の得意先別単価 → 商品の標準単価、の順。
    /// 支店の受注は請求先(本社)の単価を使う(<paramref name="customer"/> の請求先を読み込んでおく)。
    /// </summary>
    public static decimal UnitPriceFor(Customer customer, Product product, DateOnly orderedOn, IEnumerable<CustomerPrice> prices, decimal? specified = null)
    {
        if (specified is { } price) return price;
        var billing = customer.BillingTarget;
        // 有効な単価は重ならない(登録と DB の排他制約が止める)ので、あっても1つ
        return prices.SingleOrDefault(p => p.IsActive && p.Customer == billing && p.Product == product
                && p.ValidFrom <= orderedOn && (p.ValidTo is null || orderedOn <= p.ValidTo))?.UnitPrice
            ?? product.StandardPrice;
    }

    private static bool Overlaps(DateOnly from1, DateOnly? to1, DateOnly from2, DateOnly? to2) =>
        (to1 is null || from2 <= to1) && (to2 is null || from1 <= to2);
}
