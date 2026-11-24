namespace SalesCore.Application;

// 入金の API の入力と出力。金額は円。打ち消しの入金(取消)は負の額。

/// <summary>
/// 入金の登録。請求先(請求先を持たない得意先)で受ける。入金日と入金額は、省くと「入れる」よう求める
/// (既定値の 0001-01-01・0円で比べた理由にしない。段階3 1週目の振り返り)。
/// </summary>
public sealed record RegisterReceipt(string BillingCustomerCode, DateOnly? ReceivedOn, decimal? Amount);

/// <summary>
/// 入金。前受(<see cref="Unallocated"/>)は、どの請求書にも充てていない残り。
/// 取り消した入金は <see cref="IsCancelled"/> で、打ち消しの入金を <see cref="ReversedBy"/> に、打ち消しの入金は元の入金を <see cref="Reverses"/> に持つ。
/// </summary>
public sealed record ReceiptView(
    long Id, string Number, string BillingCustomerCode, DateOnly ReceivedOn, decimal Amount, decimal AllocatedAmount, decimal Unallocated,
    bool IsCancelled, ReceiptLink? Reverses, ReceiptLink? ReversedBy, IReadOnlyList<ReceiptAllocationView> Allocations);

public sealed record ReceiptLink(long Id, string Number);

/// <summary>入金を充てた請求書と額(充てた順)。</summary>
public sealed record ReceiptAllocationView(long InvoiceId, string InvoiceNumber, DateOnly ClosingDate, decimal Amount);

/// <summary>入金の一覧の1行。打ち消しの入金は、元の入金の番号を <see cref="ReversesNumber"/> に持つ。</summary>
public sealed record ReceiptSummary(
    long Id, string Number, string BillingCustomerCode, DateOnly ReceivedOn, decimal Amount, decimal Unallocated, bool IsCancelled, string? ReversesNumber);

/// <summary>未回収の請求書(登録の画面で、充てる順に出す)。未回収は今回分(今回売上＋消費税)のうち、まだ消し込んでいない額。</summary>
public sealed record OutstandingInvoiceView(long Id, string Number, DateOnly ClosingDate, decimal Outstanding);
