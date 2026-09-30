namespace SalesCore.Domain.Tests;

/// <summary>
/// 金額と端数処理。規則の定義元は docs/domain/business-rules.md の「1. 金額と数量」。
/// </summary>
public class MoneyTests
{
    // ── 四捨五入 ──
    // .NET の Math.Round・decimal.Round は既定で「銀行丸め」(0.5 は偶数の側へ)。2.5 が 2 になる。
    // 業務ルールは四捨五入なので 3。ちょうど .5 の値を必ず含める。
    [Theory]
    [InlineData("0.4", "0")]
    [InlineData("0.49", "0")]
    [InlineData("0.5", "1")]
    [InlineData("0.51", "1")]
    [InlineData("1.5", "2")]
    [InlineData("2.5", "3")]    // 銀行丸めなら 2
    [InlineData("100.5", "101")]
    [InlineData("-0.4", "0")]
    [InlineData("-0.5", "-1")]
    [InlineData("-2.5", "-3")]  // 銀行丸めなら -2
    [InlineData("999999999999998.5", "999999999999999")] // numeric(15,0) の上限の手前
    public void 円未満は四捨五入する(string amount, string expectedYen)
    {
        Assert.Equal(decimal.Parse(expectedYen), Money.Round(decimal.Parse(amount)).Yen);
    }

    // 赤伝は元の伝票の金額の符号を反転したもの。丸めが正負で対称でなければ、赤と黒が打ち消し合わない
    [Theory]
    [InlineData("0.5")]
    [InlineData("2.5")]
    [InlineData("49.995")]
    [InlineData("1234.4999")]
    public void 丸めは正負で対称になり_赤伝と黒伝が打ち消し合う(string amount)
    {
        var black = Money.Round(decimal.Parse(amount));
        var red = Money.Round(-decimal.Parse(amount));

        Assert.Equal(-black, red);
        Assert.Equal(Money.Zero, black + red);
    }

    // ── 明細金額 = 単価(小数2桁) × 数量(小数3桁) を四捨五入 ──
    // decimal の積は小数5桁まで誤差なく出るので、丸めは Money.Round の1回だけで済む
    [Theory]
    [InlineData("1.25", "2", "3")]          // 2.5 → 3(銀行丸めなら 2)
    [InlineData("33.33", "1.5", "50")]      // 49.995 → 50
    [InlineData("99.99", "0.005", "0")]     // 0.49995 → 0
    [InlineData("0.01", "0.001", "0")]      // 0.00001 → 0
    [InlineData("1980", "3", "5940")]       // 端数なし
    public void 明細金額は単価と数量の積を四捨五入する(string unitPrice, string quantity, string expectedYen)
    {
        var lineAmount = Money.Round(decimal.Parse(unitPrice) * decimal.Parse(quantity));

        Assert.Equal(decimal.Parse(expectedYen), lineAmount.Yen);
    }

    // ── 円単位の金額 ──
    [Fact]
    public void 円未満の端数がある金額はそのままでは作れない()
    {
        Assert.Throws<ArgumentException>(() => Money.Of(100.5m));
    }

    [Fact]
    public void 小数点以下がゼロなら円単位の金額として扱う()
    {
        Assert.Equal(Money.Of(100m), Money.Of(100.00m));
    }

    [Fact]
    public void 足し算と引き算と符号の反転()
    {
        Assert.Equal(Money.Of(150), Money.Of(100) + Money.Of(50));
        Assert.Equal(Money.Of(50), Money.Of(100) - Money.Of(50));
        Assert.Equal(Money.Of(-100), -Money.Of(100));
    }
}
