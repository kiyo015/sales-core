namespace SalesCore.Domain.Tests;

/// <summary>
/// 見積の状態と金額。業務ルール集「3. 販売の流れ」の「見積と得意先別単価」(2026-11-12・11-17 本人の決定)。
/// 状態は 作成中 → 提出済 → 受注済 ／ 失注 で、逆戻りしない。状態×操作の全組み合わせをここで固定する。
/// </summary>
public class QuotationTests
{
    private static readonly TaxRate Standard = TaxRate.Of(10m);
    private static readonly TaxRate Reduced = TaxRate.Of(8m);
    private static readonly Customer HeadOffice = new("C001", "本社", 31);
    private static readonly DateOnly QuotedOn = new(2030, 4, 1);
    private static readonly DateOnly ValidUntil = new(2030, 4, 30);

    private static QuotationLine Line(decimal quantity = 1m, decimal unitPrice = 100m, TaxRate? rate = null) =>
        new(quantity, unitPrice, rate ?? Standard);

    private static Quotation Draft(params QuotationLine[] lines) =>
        new(HeadOffice, QuotedOn, ValidUntil, lines.Length == 0 ? [Line()] : lines);

    // 見積をその状態まで進める(正しい操作だけを使う)
    private static Quotation In(QuotationStatus status)
    {
        var quotation = Draft();
        switch (status)
        {
            case QuotationStatus.Submitted:
                quotation.Submit();
                break;
            case QuotationStatus.Ordered:
                quotation.Submit();
                quotation.MarkOrdered(QuotedOn);
                break;
            case QuotationStatus.Lost:
                quotation.MarkLost();
                break;
        }
        Assert.Equal(status, quotation.Status);
        return quotation;
    }

    [Fact]
    public void 登録した見積は作成中で始まる()
    {
        var quotation = Draft();

        Assert.Equal((QuotationStatus.Draft, QuotedOn, ValidUntil), (quotation.Status, quotation.QuotedOn, quotation.ValidUntil));
        Assert.Same(HeadOffice, quotation.Customer);
    }

    [Fact]
    public void 明細の無い見積は作れない()
    {
        Assert.Throws<InvalidInputException>(() => new Quotation(HeadOffice, QuotedOn, ValidUntil, []));
    }

    [Fact]
    public void 有効期限は見積日より前にできず_同じ日ならよい()
    {
        Assert.Throws<InvalidInputException>(() => new Quotation(HeadOffice, QuotedOn, QuotedOn.AddDays(-1), [Line()]));
        Assert.Equal(QuotedOn, new Quotation(HeadOffice, QuotedOn, QuotedOn, [Line()]).ValidUntil);
    }

    // ── 状態×操作の全組み合わせ(2026-11-17 本人の決定) ──
    // 直す(明細の数量・単価)は作成中だけ。提出済を直す時は新しい見積を作る。
    // 提出は作成中だけ。失注は作成中と提出済。受注にできるのは提出済だけ
    public static TheoryData<QuotationStatus, string, bool> StatusOperations()
    {
        var allowed = new Dictionary<string, QuotationStatus[]>
        {
            ["直す"] = [QuotationStatus.Draft],
            ["提出"] = [QuotationStatus.Draft],
            ["失注"] = [QuotationStatus.Draft, QuotationStatus.Submitted],
            ["受注"] = [QuotationStatus.Submitted],
        };
        var data = new TheoryData<QuotationStatus, string, bool>();
        foreach (var status in Enum.GetValues<QuotationStatus>())
        {
            foreach (var (operation, statuses) in allowed)
            {
                data.Add(status, operation, statuses.Contains(status));
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(StatusOperations))]
    public void 状態ごとにできる操作が決まっている(QuotationStatus status, string operation, bool allowed)
    {
        var quotation = In(status);
        var line = quotation.Lines[0];
        Action act = operation switch
        {
            "直す" => () => quotation.ChangeLine(line, 2m, 90m),
            "提出" => quotation.Submit,
            "失注" => quotation.MarkLost,
            "受注" => () => quotation.MarkOrdered(QuotedOn),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

        if (allowed)
        {
            act();
            var expected = operation switch
            {
                "提出" => QuotationStatus.Submitted,
                "失注" => QuotationStatus.Lost,
                "受注" => QuotationStatus.Ordered,
                _ => status,
            };
            Assert.Equal(expected, quotation.Status);
        }
        else
        {
            Assert.Throws<BusinessRuleException>(act);
            Assert.Equal(status, quotation.Status); // 拒んだ時は状態を変えない
            Assert.Equal((1m, 100m), (line.Quantity, line.UnitPrice)); // 明細も変えない
        }
    }

    [Fact]
    public void 作成中の見積は明細の数量と単価を直せる()
    {
        var quotation = Draft(Line(1m, 100m));

        quotation.ChangeLine(quotation.Lines[0], 3m, 90m);

        Assert.Equal((3m, 90m, Money.Of(270m)), (quotation.Lines[0].Quantity, quotation.Lines[0].UnitPrice, quotation.Amount));
    }

    [Fact]
    public void 別の見積の明細は直せない()
    {
        var other = Draft();

        Assert.Throws<InvalidInputException>(() => Draft().ChangeLine(other.Lines[0], 2m, 90m));
    }

    [Theory]
    [InlineData("0", "100")]
    [InlineData("1.0001", "100")]
    [InlineData("1", "-0.01")]
    [InlineData("1", "100.001")]
    public void 直す時も数量と単価の決まりを守る(string quantity, string unitPrice)
    {
        var quotation = Draft(Line(1m, 100m));

        Assert.Throws<InvalidInputException>(() => quotation.ChangeLine(quotation.Lines[0], decimal.Parse(quantity), decimal.Parse(unitPrice)));
        Assert.Equal((1m, 100m), (quotation.Lines[0].Quantity, quotation.Lines[0].UnitPrice));
    }

    // ── 有効期限(日本時間の今日で判定。今日は呼ぶ側が決める) ──
    [Theory]
    [InlineData(4, 1, true)] // 見積日
    [InlineData(4, 30, true)] // 有効期限の当日
    [InlineData(5, 1, false)] // 翌日
    public void 有効期限の当日までは受注にでき_翌日からできない(int month, int day, bool allowed)
    {
        var quotation = In(QuotationStatus.Submitted);
        var today = new DateOnly(2030, month, day);

        if (allowed)
        {
            quotation.MarkOrdered(today);
            Assert.Equal(QuotationStatus.Ordered, quotation.Status);
        }
        else
        {
            var error = Assert.Throws<BusinessRuleException>(() => quotation.MarkOrdered(today));
            Assert.Contains("2030-04-30", error.Message);
            Assert.Equal(QuotationStatus.Submitted, quotation.Status);
        }
    }

    [Fact]
    public void 有効期限を過ぎた見積も失注にはできる()
    {
        var quotation = In(QuotationStatus.Submitted);

        quotation.MarkLost();

        Assert.Equal(QuotationStatus.Lost, quotation.Status);
    }

    // ── 金額と税(受注と同じ計算。明細は四捨五入、税は税率ごとに1回) ──
    [Fact]
    public void 見積の金額は明細金額の合計で_明細金額は単価と数量の積の四捨五入()
    {
        var quotation = Draft(Line(1.5m, 33.33m), Line(2m, 1.25m)); // 49.995 → 50、2.5 → 3

        Assert.Equal((Money.Of(50m), Money.Of(3m), Money.Of(53m)), (quotation.Lines[0].Amount, quotation.Lines[1].Amount, quotation.Amount));
    }

    [Fact]
    public void 税は税率ごとに1回だけ四捨五入する()
    {
        // 105円×3行(10%)は、明細ごとなら 11円×3＝33円、税率ごとなら 315円×10%＝31.5 → 32円。8% の 100円は 8円
        var quotation = Draft(Line(1m, 105m), Line(1m, 105m), Line(1m, 105m), Line(1m, 100m, Reduced));

        var tax = quotation.Tax;

        Assert.Equal(
            [(Standard, Money.Of(315m), Money.Of(32m)), (Reduced, Money.Of(100m), Money.Of(8m))],
            tax.Summaries.Select(s => (s.Rate, s.TaxableAmount, s.Tax)));
        Assert.Equal((Money.Of(415m), Money.Of(40m), Money.Of(455m)), (tax.TaxableTotal, tax.TaxTotal, tax.TotalWithTax));
    }

    [Fact]
    public void 直すと金額と税も変わる()
    {
        var quotation = Draft(Line(1m, 105m));

        quotation.ChangeLine(quotation.Lines[0], 3m, 105m);

        Assert.Equal((Money.Of(315m), Money.Of(32m)), (quotation.Amount, quotation.Tax.TaxTotal));
    }
}
