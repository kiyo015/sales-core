namespace SalesCore.Domain;

// 業務の判断を持たないマスタ。判断が要るようになったら(得意先別単価・在庫など)、それぞれのファイルに分ける。
// 入力の誤りは理由付きで返す(InvalidInputException)。画面で管理者が登録するため。長さは DB の列の長さ(Configurations)

/// <summary>マスタの文字の項目の決まり(空にできない・長さ)。</summary>
internal static class MasterInput
{
    public static string Text(string? value, string label, int maxLength, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidInputException($"{label}は空にできない。", paramName);
        }
        if (value.Length > maxLength)
        {
            throw new InvalidInputException($"{label}は{maxLength}文字まで({value.Length}文字)。", paramName);
        }
        return value;
    }
}

/// <summary>商品。標準販売単価は受注明細に写して使う(後で単価を変えても、過去の受注は変わらない)。</summary>
public sealed class Product
{
    private Product()
    {
        // DB から読み込むとき(EF Core)に使う
    }

    public Product(string code, string name, string unit, TaxCategory taxCategory, decimal standardPrice)
    {
        Code = MasterInput.Text(code, "商品コード", 20, nameof(code));
        Name = MasterInput.Text(name, "商品名", 100, nameof(name));
        Unit = MasterInput.Text(unit, "単位", 10, nameof(unit));
        // 受注明細の単価と同じ決まり(業務ルール集「1. 金額と数量」「3. 受注」)
        if (standardPrice < 0m)
        {
            throw new InvalidInputException($"標準単価は0以上にする({standardPrice})。", nameof(standardPrice));
        }
        if (standardPrice * 100m % 1m != 0m)
        {
            throw new InvalidInputException($"標準単価は小数2桁まで({standardPrice})。", nameof(standardPrice));
        }
        TaxCategory = taxCategory;
        StandardPrice = standardPrice;
    }

    public long Id { get; private set; }

    public string Code { get; private set; } = "";

    public string Name { get; private set; } = "";

    /// <summary>単位(個・箱・kg など)。</summary>
    public string Unit { get; private set; } = "";

    public TaxCategory TaxCategory { get; private set; } = null!;

    /// <summary>標準販売単価(小数2桁まで)。</summary>
    public decimal StandardPrice { get; private set; }

    public bool IsActive { get; private set; } = true;
}

/// <summary>税区分(標準・軽減・非課税など)。税率は期間ごとに <see cref="TaxRatePeriod"/> で持つ。</summary>
public sealed class TaxCategory
{
    private TaxCategory()
    {
        // DB から読み込むとき(EF Core)に使う
    }

    public TaxCategory(string code, string name)
    {
        Code = code;
        Name = name;
    }

    public long Id { get; private set; }

    public string Code { get; private set; } = "";

    public string Name { get; private set; } = "";
}

/// <summary>税区分ごと・期間ごとの税率。税率の改定に備えて期間で持つ。</summary>
public sealed class TaxRatePeriod
{
    private TaxRatePeriod()
    {
        // DB から読み込むとき(EF Core)に使う
    }

    public TaxRatePeriod(TaxCategory taxCategory, TaxRate rate, DateOnly validFrom, DateOnly? validTo = null)
    {
        if (validTo < validFrom)
        {
            throw new ArgumentException($"終了日({validTo})は開始日({validFrom})より前にできない。", nameof(validTo));
        }
        TaxCategory = taxCategory;
        Rate = rate;
        ValidFrom = validFrom;
        ValidTo = validTo;
    }

    public long Id { get; private set; }

    public TaxCategory TaxCategory { get; private set; } = null!;

    public TaxRate Rate { get; private set; }

    public DateOnly ValidFrom { get; private set; }

    /// <summary>この日まで有効。無ければ期限なし。</summary>
    public DateOnly? ValidTo { get; private set; }
}

/// <summary>倉庫。段階1では1件だけ登録して使う。</summary>
public sealed class Warehouse
{
    private Warehouse()
    {
        // DB から読み込むとき(EF Core)に使う
    }

    public Warehouse(string code, string name)
    {
        Code = MasterInput.Text(code, "倉庫コード", 20, nameof(code));
        Name = MasterInput.Text(name, "倉庫名", 100, nameof(name));
    }

    public long Id { get; private set; }

    public string Code { get; private set; } = "";

    public string Name { get; private set; } = "";

    public bool IsActive { get; private set; } = true;
}
