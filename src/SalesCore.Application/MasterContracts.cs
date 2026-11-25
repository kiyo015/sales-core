namespace SalesCore.Application;

// マスタ(得意先・商品・倉庫・税区分)の画面の入力と出力。コードで指す(id は見せない)。

/// <summary>得意先。回収条件は回収月(0=当月〜3=3か月後)と回収日(31 は月末)。</summary>
public sealed record CustomerView(string Code, string Name, int ClosingDay, string? BillingCustomerCode, bool IsActive, int CollectionMonths, int CollectionDay);

public sealed record ProductView(string Code, string Name, string Unit, string TaxCategoryCode, decimal StandardPrice, bool IsActive);

public sealed record WarehouseView(string Code, string Name, bool IsActive);

public sealed record TaxCategoryView(string Code, string Name);

/// <summary>
/// 得意先の登録。請求先を省くと自分が請求先。締め日は 5・10・15・20・25・31(月末)。
/// 回収条件は回収月(0=当月・1=翌月・2=翌々月・3=3か月後)と回収日(締め日と同じ選択肢)。省くと翌月末(2026-11-25 本人の決定)。
/// </summary>
public sealed record RegisterCustomer(string? Code, string? Name, int ClosingDay, string? BillingCustomerCode = null, int CollectionMonths = 1, int CollectionDay = 31)
{
    /// <summary>選べる締め日(31 は月末)。画面の選択肢にする(画面は Domain を直接使わない)。</summary>
    public static IReadOnlyList<int> ClosingDays { get; } = [.. SalesCore.Domain.Customer.ClosingDays.Order()];

    /// <summary>選べる回収月(0=当月 〜 3=3か月後)。</summary>
    public static IReadOnlyList<int> CollectionMonthOptions { get; } = [.. Enumerable.Range(0, SalesCore.Domain.Customer.MaxCollectionMonths + 1)];

    /// <summary>選べる回収日(31 は月末)。</summary>
    public static IReadOnlyList<int> CollectionDays { get; } = [.. SalesCore.Domain.Customer.CollectionDays.Order()];
}

/// <summary>商品の登録。税区分はコードで選ぶ。</summary>
public sealed record RegisterProduct(string? Code, string? Name, string? Unit, string? TaxCategoryCode, decimal StandardPrice);

public sealed record RegisterWarehouse(string? Code, string? Name);

/// <summary>得意先別単価。無効にする時に Id で指す(同じ得意先・商品に期間違いの単価が並ぶため)。</summary>
public sealed record CustomerPriceView(long Id, string CustomerCode, string ProductCode, decimal UnitPrice, DateOnly ValidFrom, DateOnly? ValidTo, bool IsActive);

/// <summary>得意先別単価の登録の結果。改定で閉じた前の単価と、特価の翌日から続けた単価(無ければ null)。画面の知らせはこれだけから作る。</summary>
public sealed record CustomerPriceRegistration(CustomerPriceView Registered, CustomerPriceView? Closed, CustomerPriceView? Continued);

/// <summary>得意先別単価の登録。末日を省くと無期限。</summary>
public sealed record RegisterCustomerPrice(string? CustomerCode, string? ProductCode, decimal UnitPrice, DateOnly ValidFrom, DateOnly? ValidTo);
