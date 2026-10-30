namespace SalesCore.Domain.Tests;

/// <summary>
/// 商品と倉庫の入力の決まり。画面で管理者が登録するので、誤りは理由付きで返す(InvalidInputException。API・画面は 400)。
/// 長さは DB の列の長さ、単価は業務ルール集「1. 金額と数量」(小数2桁まで)と「3. 販売の流れ」(0以上)。
/// </summary>
public class MasterTests
{
    private static readonly TaxCategory Standard = new("STD", "標準");

    [Fact]
    public void 商品を登録できる()
    {
        var product = new Product("P001", "ボルト", "箱", Standard, 105.25m);

        Assert.Equal(105.25m, product.StandardPrice);
        Assert.True(product.IsActive);
    }

    [Theory]
    [InlineData("", "ボルト", "箱")]
    [InlineData(" ", "ボルト", "箱")]
    [InlineData("P001", "", "箱")]
    [InlineData("P001", "ボルト", "")]
    public void 商品のコード_名前_単位は空にできない(string code, string name, string unit)
    {
        Assert.Throws<InvalidInputException>(() => new Product(code, name, unit, Standard, 100m));
    }

    [Theory]
    [InlineData(21, 1, 1)]
    [InlineData(1, 101, 1)]
    [InlineData(1, 1, 11)]
    public void 商品のコードは20文字_名前は100文字_単位は10文字まで(int code, int name, int unit)
    {
        var error = Assert.Throws<InvalidInputException>(
            () => new Product(new string('P', code), new string('名', name), new string('箱', unit), Standard, 100m));

        Assert.Contains("文字まで", error.Message);
    }

    [Fact]
    public void 商品のコード20文字_名前100文字_単位10文字なら登録できる()
    {
        var product = new Product(new string('P', 20), new string('名', 100), new string('箱', 10), Standard, 100m);

        Assert.Equal(10, product.Unit.Length);
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("100.001")]
    public void 標準単価は0以上で小数2桁まで(string price)
    {
        Assert.Throws<InvalidInputException>(() => new Product("P001", "ボルト", "箱", Standard, decimal.Parse(price)));
    }

    [Fact]
    public void 標準単価は0円でもよい()
    {
        Assert.Equal(0m, new Product("P001", "見本", "個", Standard, 0m).StandardPrice);
    }

    // DB の列は numeric(15,2)(整数部13桁)。超えると保存の時に桁あふれ(理由の無い 500)になる(PR #14 の AIレビューの Minor)
    [Fact]
    public void 標準単価は整数部13桁まで()
    {
        Assert.Equal(9_999_999_999_999.99m, new Product("P001", "ボルト", "箱", Standard, 9_999_999_999_999.99m).StandardPrice);
        Assert.Throws<InvalidInputException>(() => new Product("P001", "ボルト", "箱", Standard, 10_000_000_000_000m));
        Assert.Throws<InvalidInputException>(() => new Product("P001", "ボルト", "箱", Standard, decimal.MaxValue)); // 桁の確かめで桁あふれしない
    }

    // コピーで末尾に空白が入ると、一覧では見分けが付かない別のコードになる(PR #14 の AIレビューの Minor)
    [Theory]
    [InlineData("P001 ")]
    [InlineData(" P001")]
    public void コードの前後に空白は入れられない(string code)
    {
        var error = Assert.Throws<InvalidInputException>(() => new Product(code, "ボルト", "箱", Standard, 1m));

        Assert.Contains("前後に空白", error.Message);
    }

    // DB の varchar(n) は文字数で数える。「𠮷」(サロゲートペア)も1文字(PR #14 の AIレビューの Minor)
    [Fact]
    public void 長さは文字数で数える()
    {
        var name = string.Concat(Enumerable.Repeat("𠮷", 100));

        Assert.Equal(name, new Warehouse("W01", name).Name);
        Assert.Throws<InvalidInputException>(() => new Warehouse("W01", name + "𠮷"));
    }

    [Theory]
    [InlineData("", "本社倉庫")]
    [InlineData("W01", " ")]
    public void 倉庫のコードと名前は空にできない(string code, string name)
    {
        Assert.Throws<InvalidInputException>(() => new Warehouse(code, name));
    }

    [Theory]
    [InlineData(21, 1)]
    [InlineData(1, 101)]
    public void 倉庫のコードは20文字_名前は100文字まで(int code, int name)
    {
        Assert.Throws<InvalidInputException>(() => new Warehouse(new string('W', code), new string('名', name)));
    }
}
