namespace SalesCore.Domain;

/// <summary>
/// 消費税率(パーセント)。10% なら 10。小数2桁まで(DBは numeric(5,2))。
/// </summary>
public readonly record struct TaxRate
{
    private TaxRate(decimal percent) => Percent = percent;

    public decimal Percent { get; }

    public static TaxRate Of(decimal percent)
    {
        if (percent is < 0m or > 100m)
        {
            throw new ArgumentException($"税率は 0〜100 の範囲で指定する({percent})。", nameof(percent));
        }
        if (percent * 100m != decimal.Truncate(percent * 100m))
        {
            throw new ArgumentException($"税率は小数2桁まで({percent})。", nameof(percent));
        }
        return new(percent);
    }

    /// <summary>対象額に対する税額。四捨五入は <see cref="Money.Round"/> に任せる(丸めは Money の中だけ)。</summary>
    public Money TaxOn(Money taxableAmount) => Money.Round(taxableAmount.Yen * Percent / 100m);

    public override string ToString() => $"{Percent:0.##}%";
}
