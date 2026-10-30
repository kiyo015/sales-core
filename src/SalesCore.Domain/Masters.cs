namespace SalesCore.Domain;

// 業務の判断を持たないマスタ。判断が要るようになったら(得意先別単価・在庫など)、それぞれのファイルに分ける。
// 入力の誤りは理由付きで返す(InvalidInputException)。画面で管理者が登録するため。長さは DB の列の長さ(Configurations)

/// <summary>マスタの文字の項目の決まり(空にできない・長さ・コードの前後の空白)。</summary>
internal static class MasterInput
{
    /// <summary>コード。前後に空白があれば拒む(一覧では見分けが付かない別のコードになるため。PR #14 の AIレビューの Minor)。</summary>
    public static string Code(string? value, string label, string paramName)
    {
        var code = Text(value, label, 20, paramName);
        if (code != code.Trim())
        {
            throw new InvalidInputException($"{label}の前後に空白を入れない(「{code}」)。", paramName);
        }
        return code;
    }

    public static string Text(string? value, string label, int maxLength, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidInputException($"{label}は空にできない。", paramName);
        }
        // DB の varchar(n) と同じく文字数で数える(Length は UTF-16 の単位で、「𠮷」などを2と数える)
        var length = value.EnumerateRunes().Count();
        if (length > maxLength)
        {
            throw new InvalidInputException($"{label}は{maxLength}文字まで({length}文字)。", paramName);
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
        Code = MasterInput.Code(code, "商品コード", nameof(code));
        Name = MasterInput.Text(name, "商品名", 100, nameof(name));
        Unit = MasterInput.Text(unit, "単位", 10, nameof(unit));
        // 受注明細の単価と同じ決まり(業務ルール集「1. 金額と数量」「3. 販売の流れ」)
        if (standardPrice < 0m)
        {
            throw new InvalidInputException($"標準単価は0以上にする({standardPrice})。", nameof(standardPrice));
        }
        // DB の列は numeric(15,2)。超えると保存の時に桁あふれになる。端数の確かめ(×100)より先に見る(大きい値で桁あふれしないように)
        if (standardPrice >= 10_000_000_000_000m)
        {
            throw new InvalidInputException($"標準単価は整数部13桁まで({standardPrice})。", nameof(standardPrice));
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
        Code = MasterInput.Code(code, "倉庫コード", nameof(code));
        Name = MasterInput.Text(name, "倉庫名", 100, nameof(name));
    }

    public long Id { get; private set; }

    public string Code { get; private set; } = "";

    public string Name { get; private set; } = "";

    public bool IsActive { get; private set; } = true;
}
