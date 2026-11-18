namespace SalesCore.Application;

// 見積の API の入力と出力。金額は円、税率は % の数値。状態は英語の名前(Draft など)。

/// <summary>
/// 見積の登録。有効期限は必ず入れる(2026-11-18 本人の決定)。単価を省くと、見積日に有効な請求先の得意先別単価、無ければ商品の標準単価。
/// 税率は見積日時点の、商品の税区分の税率。見積日と有効期限は、省くと「入れる」よう求める(既定値の 0001-01-01 で比べた理由にしない)。
/// </summary>
public sealed record CreateQuotation(string CustomerCode, DateOnly? QuotedOn, DateOnly? ValidUntil, IReadOnlyList<CreateQuotationLine>? Lines);

public sealed record CreateQuotationLine(string ProductCode, decimal Quantity, decimal? UnitPrice = null);

/// <summary>作成中の見積の明細の数量と単価を直す。単価は省くと「入れる」よう求める(既定値の 0 で 0円の明細にしない)。</summary>
public sealed record ChangeQuotationLine(decimal Quantity, decimal? UnitPrice);

/// <summary>見積。金額は税抜合計、税額は税率ごとに1回丸めた合計。</summary>
public sealed record QuotationView(
    long Id, string Number, string Status, string CustomerCode, DateOnly QuotedOn, DateOnly ValidUntil, decimal Amount, decimal TaxAmount,
    IReadOnlyList<QuotationLineView> Lines);

public sealed record QuotationLineView(long Id, string ProductCode, decimal Quantity, decimal UnitPrice, decimal TaxRate);
