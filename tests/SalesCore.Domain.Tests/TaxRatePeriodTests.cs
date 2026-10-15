namespace SalesCore.Domain.Tests;

/// <summary>
/// 税区分ごと・期間ごとの税率。規則の定義元は docs/domain/business-rules.md の「2. 消費税」(税率は税区分と期間で決まる)。
/// 期間の重なりは DB の排他制約で止める(PersistenceMappingTests)。ここでは1件の中で決まることだけを見る。
/// </summary>
public class TaxRatePeriodTests
{
    private static readonly TaxCategory Standard = new("STD", "標準");

    [Fact]
    public void 終了日は開始日より前にできない()
    {
        Assert.Throws<ArgumentException>(
            () => new TaxRatePeriod(Standard, TaxRate.Of(10m), new DateOnly(2026, 4, 1), new DateOnly(2026, 3, 31)));
    }

    [Fact]
    public void 開始日と同じ日に終わる期間は作れる()
    {
        var period = new TaxRatePeriod(Standard, TaxRate.Of(10m), new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 1));

        Assert.Equal(period.ValidFrom, period.ValidTo);
    }
}
