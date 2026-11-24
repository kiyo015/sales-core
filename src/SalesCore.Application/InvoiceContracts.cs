namespace SalesCore.Application;

// 請求書の API の入力と出力。金額は円、税率は % の数値。

/// <summary>締めて請求書を作る。請求先(請求先を持たない得意先)と、その請求先の締め日を渡す。</summary>
public sealed record CloseInvoice(string BillingCustomerCode, DateOnly ClosingDate);

/// <summary>
/// 請求書。<see cref="ReceivedAmount"/> は繰越の形式の「前回からの入金額」(繰越の請求を作る Day57 まで 0)。
/// <see cref="AllocatedAmount"/> は、この請求書の今回分(今回売上＋消費税)に消し込んだ入金の額で、<see cref="Outstanding"/> は今回分の未回収。
/// 消し込んでも請求額は変わらない(消し込んだ額は回収の状態)。
/// </summary>
public sealed record InvoiceView(
    long Id, string Number, string Status, string BillingCustomerCode,
    DateOnly ClosingDate, DateOnly PeriodFrom, DateOnly PeriodTo, DateOnly? DueDate,
    decimal PreviousBalance, decimal ReceivedAmount, decimal CarriedOver, decimal SalesAmount, decimal TaxAmount, decimal TotalAmount,
    IReadOnlyList<InvoiceTaxSummaryView> TaxSummaries, IReadOnlyList<string> SalesRecordNumbers,
    decimal AllocatedAmount, decimal Outstanding);

/// <summary>請求書の一覧の1行。金額は請求額(税込)と、今回分の未回収。</summary>
public sealed record InvoiceSummary(long Id, string Number, string Status, string BillingCustomerCode, DateOnly ClosingDate, decimal TotalAmount, decimal Outstanding);

/// <summary>締められる請求先(請求先を持たない得意先)。締め日の初期値に、日本時間の今日以前で最近の締め日を添える。</summary>
public sealed record BillingCustomerView(string Code, string Name, int ClosingDay, bool IsActive, DateOnly LatestClosingDate);

/// <summary>税率ごとの対象額と税額(税率ごとに1回だけ四捨五入)。</summary>
public sealed record InvoiceTaxSummaryView(decimal TaxRate, decimal TaxableAmount, decimal TaxAmount);
