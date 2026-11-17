namespace SalesCore.Domain.Tests;

/// <summary>
/// 得意先別単価(期間つき)と受注の単価の決め方。業務ルール集「3. 販売の流れ」の「見積と得意先別単価」(2026-11-13・11-16 本人の決定)。
/// 期間は初日と末日を含み、末日が空なら無期限。日付は境目(初日の前日・初日・末日・翌日)を必ず入れる。
/// </summary>
public class CustomerPriceTests
{
    private static readonly TaxCategory Standard = new("STD", "標準");
    private static readonly Product Bolt = new("P001", "ボルト", "箱", Standard, 100m);
    private static readonly Product Nut = new("P002", "ナット", "箱", Standard, 50m);
    private static readonly Customer HeadOffice = new("C001", "本社", 31);
    private static readonly Customer Branch = new("C002", "支店", 31, HeadOffice);
    private static readonly Customer Other = new("C003", "別の得意先", 31);

    private static DateOnly D(int month, int day) => new(2030, month, day);

    private static CustomerPrice Register(decimal price, DateOnly from, DateOnly? to, params CustomerPrice[] existing) =>
        CustomerPrice.Register(HeadOffice, Bolt, price, from, to, existing).Registered;

    [Fact]
    public void 請求先に期間つきの単価を登録できる()
    {
        var price = Register(90m, D(4, 1), D(9, 30));

        Assert.Equal((90m, D(4, 1), D(9, 30), true), (price.UnitPrice, price.ValidFrom, price.ValidTo, price.IsActive));
        Assert.Same(HeadOffice, price.Customer);
    }

    [Fact]
    public void 支店には登録できず_本来の請求先を示す()
    {
        var error = Assert.Throws<InvalidInputException>(() => CustomerPrice.Register(Branch, Bolt, 90m, D(4, 1), null, []).Registered);

        Assert.Contains("C001", error.Message);
    }

    [Fact]
    public void 末日は初日より前にできず_同じ日ならよい()
    {
        Assert.Throws<InvalidInputException>(() => Register(90m, D(4, 1), D(3, 31)));
        Assert.Equal(D(4, 1), Register(90m, D(4, 1), D(4, 1)).ValidTo);
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("100.001")]
    [InlineData("10000000000000")]
    public void 単価は0以上_小数2桁まで_整数部13桁まで(string price)
    {
        Assert.Throws<InvalidInputException>(() => Register(decimal.Parse(price), D(4, 1), null));
    }

    [Fact]
    public void 改定すると_無期限の前の単価を新しい初日の前日で閉じる()
    {
        var before = Register(90m, D(4, 1), null);

        var after = Register(80m, D(10, 1), null, before);

        Assert.Equal(D(9, 30), before.ValidTo);
        Assert.Null(after.ValidTo);
    }

    // 特価のように末日つきで登録した時は、前の単価を特価の翌日から続ける(2026-11-16 本人の決定。PR #24 の AIレビュー)
    [Theory]
    [InlineData(9, 30, 90)] // 閉じた前の単価の末日
    [InlineData(10, 1, 80)] // 特価の初日
    [InlineData(10, 31, 80)] // 特価の末日
    [InlineData(11, 1, 90)] // 特価の翌日から前の単価に戻る
    public void 末日つきで登録すると_前の無期限の単価を翌日から続ける(int month, int day, int expected)
    {
        var before = Register(90m, D(4, 1), null);

        var (special, continued) = CustomerPrice.Register(HeadOffice, Bolt, 80m, D(10, 1), D(10, 31), [before]);

        Assert.Equal((90m, D(11, 1), (DateOnly?)null, true), (continued!.UnitPrice, continued.ValidFrom, continued.ValidTo, continued.IsActive));
        Assert.Equal(expected, CustomerPrice.UnitPriceFor(HeadOffice, Bolt, D(month, day), [before, special, continued]));
    }

    [Fact]
    public void 無期限で改定した時と_前に無期限の単価が無い時は_続きを作らない()
    {
        // 確かめるごとに前の単価を作り直す(登録すると前の単価が閉じ、次の確かめが閉じる分岐を通らなくなる)
        Assert.Null(CustomerPrice.Register(HeadOffice, Bolt, 80m, D(10, 1), null, [Register(90m, D(4, 1), null)]).Continued);
        Assert.Null(CustomerPrice.Register(HeadOffice, Nut, 40m, D(10, 1), D(10, 31), [Register(90m, D(4, 1), null)]).Continued);
        var before = Register(90m, D(4, 1), null);
        Assert.Null(CustomerPrice.Register(HeadOffice, Bolt, 80m, D(10, 1), DateOnly.MaxValue, [before]).Continued); // 翌日が無い
        Assert.Equal(D(9, 30), before.ValidTo); // 閉じる分岐を通った
    }

    // 初日が日付の最小値でも、前日の計算で理由の無い例外(500)にしない(PR #24 の AIレビュー3・4回目の Minor)
    [Fact]
    public void 初日が日付の最小値でも_登録でき_重なれば理由を返す()
    {
        Assert.Equal(DateOnly.MinValue, Register(90m, DateOnly.MinValue, null).ValidFrom);
        Assert.Throws<BusinessRuleException>(() => Register(80m, DateOnly.MinValue, null, Register(90m, D(4, 1), null)));
    }

    [Fact]
    public void 期間を決めた単価とは_末日が重なると拒み_翌日からならよい()
    {
        var before = Register(90m, D(4, 1), D(9, 30));

        Assert.Throws<BusinessRuleException>(() => Register(80m, D(9, 30), null, before));
        Assert.Equal(D(10, 1), Register(80m, D(10, 1), null, before).ValidFrom);
        Assert.Equal(D(9, 30), before.ValidTo); // 閉じ直さない
    }

    [Theory]
    [InlineData(4, 1, null, null)] // 同じ初日(閉じると期間が空になる)
    [InlineData(3, 31, null, null)] // 前の単価より前から無期限
    [InlineData(3, 1, 4, 1)] // 前の単価の初日に掛かる
    public void 無期限の前の単価の初日以前から始まり_重なる登録は拒む(int month, int day, int? toMonth, int? toDay)
    {
        var before = Register(90m, D(4, 1), null);
        DateOnly? to = toMonth is null ? null : D(toMonth.Value, toDay!.Value);

        Assert.Throws<BusinessRuleException>(() => Register(80m, D(month, day), to, before));
        Assert.Null(before.ValidTo); // 拒んだ時は前の単価を閉じない
    }

    [Fact]
    public void 無期限の前の単価より前に_重ならない期間なら登録できる()
    {
        var before = Register(90m, D(4, 1), null);

        Register(80m, D(3, 1), D(3, 31), before);

        Assert.Null(before.ValidTo);
    }

    [Fact]
    public void 無効にした単価は重なりの判定から外し_閉じない()
    {
        var mistaken = Register(900m, D(4, 1), null);
        mistaken.Invalidate();

        var corrected = Register(90m, D(4, 1), null, mistaken);

        Assert.Equal(D(4, 1), corrected.ValidFrom);
        Assert.Null(mistaken.ValidTo);
        Assert.False(mistaken.IsActive);
    }

    [Fact]
    public void 別の商品と別の得意先の単価とは重ならない()
    {
        var nut = CustomerPrice.Register(HeadOffice, Nut, 40m, D(4, 1), null, []).Registered;
        var other = CustomerPrice.Register(Other, Bolt, 70m, D(4, 1), null, []).Registered;

        Register(90m, D(4, 1), null, nut, other);

        Assert.Null(nut.ValidTo);
        Assert.Null(other.ValidTo);
    }

    [Fact]
    public void 無効にした単価は_もう一度無効にできない()
    {
        var price = Register(90m, D(4, 1), null);
        price.Invalidate();

        Assert.Throws<BusinessRuleException>(price.Invalidate);
    }

    [Theory]
    [InlineData(3, 31, 100)] // 初日の前日は標準単価
    [InlineData(4, 1, 90)] // 初日
    [InlineData(9, 30, 90)] // 末日
    [InlineData(10, 1, 100)] // 末日の翌日は標準単価
    public void 受注の単価は_受注日に有効な得意先別単価_無ければ標準単価(int month, int day, int expected)
    {
        var price = Register(90m, D(4, 1), D(9, 30));

        Assert.Equal(expected, CustomerPrice.UnitPriceFor(HeadOffice, Bolt, D(month, day), [price]));
    }

    [Fact]
    public void 末日が空の単価は_ずっと先の受注日にも使う()
    {
        var price = Register(90m, D(4, 1), null);

        Assert.Equal(90m, CustomerPrice.UnitPriceFor(HeadOffice, Bolt, new DateOnly(2099, 12, 31), [price]));
    }

    [Fact]
    public void 明細で指定した単価が_得意先別単価より先()
    {
        var price = Register(90m, D(4, 1), null);

        Assert.Equal(85m, CustomerPrice.UnitPriceFor(HeadOffice, Bolt, D(5, 1), [price], specified: 85m));
    }

    // 得意先と商品はコードで照らす。別の DbContext や AsNoTracking で読んだもの(別のインスタンス)でも見落とさない(PR #24 の AIレビュー)
    [Fact]
    public void 別に読み込んだ同じコードの得意先と商品でも_単価を見つける()
    {
        var price = Register(90m, D(4, 1), null);
        var headOffice = new Customer("C001", "本社", 31);
        var bolt = new Product("P001", "ボルト", "箱", Standard, 100m);

        Assert.Equal(90m, CustomerPrice.UnitPriceFor(headOffice, bolt, D(5, 1), [price]));
        Assert.Throws<BusinessRuleException>(() => CustomerPrice.Register(headOffice, bolt, 80m, D(4, 1), null, [price]));
    }

    [Fact]
    public void 支店の受注は請求先の単価を使う()
    {
        var headOffice = Register(90m, D(4, 1), null);

        Assert.Equal(90m, CustomerPrice.UnitPriceFor(Branch, Bolt, D(5, 1), [headOffice]));
    }

    [Fact]
    public void 無効の単価_別の商品_別の得意先の単価は使わない()
    {
        var invalid = Register(90m, D(4, 1), null);
        invalid.Invalidate();
        var nut = CustomerPrice.Register(HeadOffice, Nut, 40m, D(4, 1), null, []).Registered;
        var other = CustomerPrice.Register(Other, Bolt, 70m, D(4, 1), null, []).Registered;

        Assert.Equal(100m, CustomerPrice.UnitPriceFor(HeadOffice, Bolt, D(5, 1), [invalid, nut, other]));
    }
}
