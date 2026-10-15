namespace SalesCore.Domain;

/// <summary>
/// 円単位の金額。円未満の端数を持たない。
/// 端数の出る計算結果(単価×数量など)は <see cref="Round"/> で円単位にする。
/// <b>丸めはこの型の中だけで行う</b>(docs/domain/business-rules.md の「端数処理」)。
/// </summary>
public readonly record struct Money
{
    private Money(decimal yen) => Yen = yen;

    /// <summary>円単位の金額。小数部は常に 0。</summary>
    public decimal Yen { get; }

    public static Money Zero => new(0m);

    /// <summary>円単位の金額を作る。円未満の端数があれば、丸めずに例外にする(どこかで丸め忘れている)。</summary>
    public static Money Of(decimal yen)
    {
        if (yen % 1m != 0m)
        {
            throw new ArgumentException(
                $"円未満の端数がある({yen})。端数の出る計算結果は Money.Round で丸めること。", nameof(yen));
        }
#pragma warning disable RS0030 // 端数処理の API を使ってよいのは Money.cs だけ(src/Directory.Build.props が他のファイルでの抑止を止める)
        return new(decimal.Truncate(yen)); // 100.00 と 100 を同じ表現にそろえる
#pragma warning restore RS0030
    }

    /// <summary>
    /// 円未満を四捨五入する。0.5 は 0 から遠い側へ(2.5 → 3、-2.5 → -3)。
    /// .NET の既定は銀行丸め(2.5 → 2)なので、丸め方を必ず明示する。
    /// 0 から遠い側に寄せるので正負で対称になり、赤伝(符号を反転した伝票)と黒伝が打ち消し合う。
    /// </summary>
#pragma warning disable RS0030 // 業務ルールの丸めの唯一の実装
    public static Money Round(decimal amount) => new(decimal.Round(amount, 0, MidpointRounding.AwayFromZero));
#pragma warning restore RS0030

    public static Money operator +(Money left, Money right) => new(left.Yen + right.Yen);

    public static Money operator -(Money left, Money right) => new(left.Yen - right.Yen);

    public static Money operator -(Money value) => new(-value.Yen);

    public override string ToString() => $"{Yen:#,0}円";
}
