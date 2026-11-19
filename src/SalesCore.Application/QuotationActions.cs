using SalesCore.Domain;

namespace SalesCore.Application;

/// <summary>
/// 見積の画面に出すボタンと「期限切れ」の表示。状態は Domain の <see cref="QuotationStatusRules"/>、有効期限は <see cref="Quotation.IsExpired"/>、
/// 役割は入口の検査と同じ表(<see cref="Permissions"/>)で決める。隠すのは使い勝手のためで、守りはユースケースの入口。
/// </summary>
public static class QuotationActions
{
    /// <summary>
    /// 有効期限を過ぎた、作成中か提出済の見積(提出も受注化もできない)。状態は自動では変えず、失注は人が押す(2026-11-19 本人の決定)。
    /// 受注済・失注は期限に関係なく終わっているので、期限切れとは出さない。
    /// </summary>
    public static bool IsExpired(string status, DateOnly validUntil, DateOnly today) =>
        StatusOf(status) is QuotationStatus.Draft or QuotationStatus.Submitted && Quotation.IsExpired(validUntil, today);

    public static bool CanChange(ICurrentUser user, QuotationView quotation) =>
        StatusOf(quotation.Status).CanChange() && Permissions.Allows(user, Operation.ManageQuotation);

    public static bool CanSubmit(ICurrentUser user, QuotationView quotation, DateOnly today) =>
        StatusOf(quotation.Status).CanSubmit() && !Quotation.IsExpired(quotation.ValidUntil, today) && Permissions.Allows(user, Operation.ManageQuotation);

    public static bool CanMarkLost(ICurrentUser user, QuotationView quotation) =>
        StatusOf(quotation.Status).CanMarkLost() && Permissions.Allows(user, Operation.ManageQuotation);

    public static bool CanOrder(ICurrentUser user, QuotationView quotation, DateOnly today) =>
        StatusOf(quotation.Status).CanOrder() && !Quotation.IsExpired(quotation.ValidUntil, today) && Permissions.Allows(user, Operation.ManageQuotation);

    private static QuotationStatus StatusOf(string status) => Enum.Parse<QuotationStatus>(status);
}
