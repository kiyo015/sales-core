namespace SalesCore.Domain.Tests;

/// <summary>
/// 商品と倉庫の入力の決まり。画面で管理者が登録するので、誤りは理由付きで返す(InvalidInputException。API・画面は 400)。
/// 長さは DB の列の長さ、単価は業務ルール集「1. 金額と数量」(小数2桁まで)と「3. 受注」(0以上)。
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
