using SalesCore.Application;

namespace SalesCore.Web.Components.Pages.Quotations;

/// <summary>見積の状態の表示名(業務ルール集「3. 販売の流れ」の名前)。ユースケースは英語の名前(Draft など)で返す。</summary>
public static class QuotationStatusText
{
    // 状態を足したら、ここにも足す(QuotationPageTests が止める)
    public static IReadOnlyDictionary<string, string> Labels { get; } = new Dictionary<string, string>
    {
        ["Draft"] = "作成中",
        ["Submitted"] = "提出済",
        ["Ordered"] = "受注済",
        ["Lost"] = "失注",
    };

    /// <summary>状態の表示名。有効期限を過ぎた作成中・提出済は「（期限切れ）」を付ける(状態は変わらない。2026-11-19 本人の決定)。</summary>
    public static string Of(string status, DateOnly validUntil, DateOnly today) =>
        Labels.GetValueOrDefault(status, status) + (QuotationActions.IsExpired(status, validUntil, today) ? "（期限切れ）" : "");
}
