namespace SalesCore.Domain.Tests;

/// <summary>
/// 消費税。規則の定義元は docs/domain/business-rules.md の「端数処理」「2. 消費税」。
/// 税額は税率ごとに1回だけ四捨五入する(適格請求書の要件)。明細ごとには丸めない。
/// </summary>
public class TaxCalculatorTests
{
    private static readonly TaxRate Standard = TaxRate.Of(10m);
    private static readonly TaxRate Reduced = TaxRate.Of(8m);
    private static readonly TaxRate Exempt = TaxRate.Of(0m);

    private static TaxableLine Line(decimal yen, TaxRate rate) => new(Money.Of(yen), rate);

    [Fact]
    public void 税額は対象額に税率を掛けて四捨五入する()
    {
        var breakdown = TaxCalculator.Calculate([Line(1000, Standard)]);

        var summary = Assert.Single(breakdown.Summaries);
        Assert.Equal(Standard, summary.Rate);
        Assert.Equal(Money.Of(1000), summary.TaxableAmount);
        Assert.Equal(Money.Of(100), summary.Tax);
    }

    // 105円の明細3行。明細ごとに丸めると 10.5 → 11 が3回で 33円。
    // 税率ごとに1回なら 315 × 10% = 31.5 → 32円。インボイスの要件は後者
    [Fact]
    public void 税額は明細ごとではなく税率ごとに1回だけ丸める()
    {
        var breakdown = TaxCalculator.Calculate([Line(105, Standard), Line(105, Standard), Line(105, Standard)]);

        var summary = Assert.Single(breakdown.Summaries);
        Assert.Equal(Money.Of(315), summary.TaxableAmount);
        Assert.Equal(Money.Of(32), summary.Tax);
    }

    [Fact]
    public void 税率が混在する請求書は税率ごとに集計し_高い税率から並べる()
    {
        var breakdown = TaxCalculator.Calculate(
            [Line(300, Reduced), Line(1000, Standard), Line(280, Reduced), Line(500, Standard)]);

        Assert.Collection(
            breakdown.Summaries,
            s => Assert.Equal(new TaxSummary(Standard, Money.Of(1500), Money.Of(150)), s),
            s => Assert.Equal(new TaxSummary(Reduced, Money.Of(580), Money.Of(46)), s)); // 46.4 → 46
        Assert.Equal(Money.Of(2080), breakdown.TaxableTotal);
        Assert.Equal(Money.Of(196), breakdown.TaxTotal);
        Assert.Equal(Money.Of(2276), breakdown.TotalWithTax);
    }

    // ちょうど .5 円になる対象額。10% なら 5円・25円(0.5・2.5)。銀行丸めだと 0円・2円になる
    [Theory]
    [InlineData("5", "1")]
    [InlineData("25", "3")]
    [InlineData("-25", "-3")] // 赤伝だけの請求書。負の対象額も 0 から遠い側へ
    public void ちょうど半円の税額は0から遠い側へ寄せる(string taxableYen, string expectedTaxYen)
    {
        var breakdown = TaxCalculator.Calculate([Line(decimal.Parse(taxableYen), Standard)]);

        Assert.Equal(Money.Of(decimal.Parse(expectedTaxYen)), breakdown.TaxTotal);
    }

    [Fact]
    public void 赤伝と黒伝が同じ請求書にあれば_対象額も税額も打ち消し合う()
    {
        var breakdown = TaxCalculator.Calculate([Line(105, Standard), Line(-105, Standard)]);

        var summary = Assert.Single(breakdown.Summaries);
        Assert.Equal(Money.Zero, summary.TaxableAmount);
        Assert.Equal(Money.Zero, summary.Tax);
    }

    [Fact]
    public void 税率0パーセントの明細は対象額だけを集計し_税額は0円()
    {
        var breakdown = TaxCalculator.Calculate([Line(1234, Exempt), Line(1000, Standard)]);

        Assert.Equal(new TaxSummary(Exempt, Money.Of(1234), Money.Zero), breakdown.Summaries[^1]);
        Assert.Equal(Money.Of(100), breakdown.TaxTotal);
    }

    [Fact]
    public void 明細が無ければ集計も無く_合計は0円()
    {
        var breakdown = TaxCalculator.Calculate([]);

        Assert.Empty(breakdown.Summaries);
        Assert.Equal(Money.Zero, breakdown.TotalWithTax);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("100.01")]
    [InlineData("10.005")] // DBは numeric(5,2)。小数3桁は持てない
    public void 範囲外や小数3桁以上の税率は作れない(string percent)
    {
        Assert.Throws<ArgumentException>(() => TaxRate.Of(decimal.Parse(percent)));
    }

    // ── ランダムな請求書 1万件 ──
    // 期待値は製品コードと別の方法(long だけの整数計算)で出す。同じ式で作ると誤りも写るため。
    // 乱数の種は固定。落ちたときに同じ請求書で再現できる
    [Fact]
    public void ランダムな請求書でも_税率ごとの対象額と税額と請求額が常に合う()
    {
        var random = new Random(20261001);
        int[] basisPoints = [1000, 800, 0]; // 10%・8%・0%(1万分率)

        for (var invoice = 0; invoice < 10_000; invoice++)
        {
            var lines = new List<TaxableLine>();
            var taxableByRate = new Dictionary<int, long>();
            var lineCount = random.Next(1, 31);
            for (var i = 0; i < lineCount; i++)
            {
                var bp = basisPoints[random.Next(basisPoints.Length)];
                long yen = random.Next(-50_000, 500_001); // 赤伝(負)も混ぜる
                lines.Add(Line(yen, TaxRate.Of(bp / 100m)));
                taxableByRate[bp] = taxableByRate.GetValueOrDefault(bp) + yen;
            }

            var breakdown = TaxCalculator.Calculate(lines);

            Assert.Equal(taxableByRate.Count, breakdown.Summaries.Count);
            long expectedTaxTotal = 0;
            foreach (var summary in breakdown.Summaries)
            {
                var bp = (int)(summary.Rate.Percent * 100m);
                var taxable = taxableByRate[bp];
                var expectedTax = RoundHalfAwayFromZero(taxable * bp, 10_000);
                expectedTaxTotal += expectedTax;

                Assert.Equal(taxable, (long)summary.TaxableAmount.Yen);
                Assert.Equal(expectedTax, (long)summary.Tax.Yen);
            }

            var lineTotal = lines.Sum(l => (long)l.Amount.Yen);
            Assert.Equal(lineTotal, (long)breakdown.TaxableTotal.Yen);          // 明細の合計 = 税抜合計
            Assert.Equal(expectedTaxTotal, (long)breakdown.TaxTotal.Yen);
            Assert.Equal(lineTotal + expectedTaxTotal, (long)breakdown.TotalWithTax.Yen); // 請求額
        }
    }

    // 整数だけの四捨五入(0.5 は 0 から遠い側)。decimal も Money も使わない
    private static long RoundHalfAwayFromZero(long numerator, long denominator)
    {
        var magnitude = (Math.Abs(numerator) * 2 + denominator) / (denominator * 2);
        return Math.Sign(numerator) * magnitude;
    }
}
