namespace SalesCore.Domain;

/// <summary>税額計算の対象になる明細1行。金額(税抜)と、その明細の税率。</summary>
public readonly record struct TaxableLine(Money Amount, TaxRate Rate);

/// <summary>税率ごとの集計。請求書の <c>invoice_tax_summaries</c> の1行にあたる。</summary>
public readonly record struct TaxSummary(TaxRate Rate, Money TaxableAmount, Money Tax);

/// <summary>請求書1枚ぶんの税額の内訳。税率の高い順に並ぶ。</summary>
public sealed record TaxBreakdown(IReadOnlyList<TaxSummary> Summaries)
{
    /// <summary>税抜合計(明細金額の合計)。</summary>
    public Money TaxableTotal => Summaries.Aggregate(Money.Zero, (total, s) => total + s.TaxableAmount);

    /// <summary>消費税の合計。税率ごとに丸めた税額を足したもの。</summary>
    public Money TaxTotal => Summaries.Aggregate(Money.Zero, (total, s) => total + s.Tax);

    public Money TotalWithTax => TaxableTotal + TaxTotal;
}

public static class TaxCalculator
{
    /// <summary>
    /// 明細を税率ごとにまとめ、<b>税率ごとに1回だけ</b>税額を四捨五入する(適格請求書の要件)。
    /// 明細ごとに税額を丸めて合計してはならない。105円の明細3行(10%)なら、
    /// 明細ごとでは 11円×3＝33円、税率ごとでは 315円×10%＝31.5 → 32円になり、1円ずれる。
    /// </summary>
    public static TaxBreakdown Calculate(IEnumerable<TaxableLine> lines) =>
        new(lines
            .GroupBy(line => line.Rate)
            .OrderByDescending(group => group.Key.Percent)
            .Select(group =>
            {
                var taxableAmount = group.Aggregate(Money.Zero, (total, line) => total + line.Amount);
                return new TaxSummary(group.Key, taxableAmount, group.Key.TaxOn(taxableAmount));
            })
            .ToList());
}
