namespace SalesCore.Web.Components.Pages.Invoices;

/// <summary>請求書の状態の表示名(業務ルール集「状態」の名前)。ユースケースは英語の名前(Confirmed など)で返す。</summary>
public static class InvoiceStatusText
{
    // 状態を足したら、ここにも足す(InvoicePageTests が止める)
    public static IReadOnlyDictionary<string, string> Labels { get; } = new Dictionary<string, string>
    {
        ["Confirmed"] = "確定",
        ["Issued"] = "発行済",
        ["Cancelled"] = "取消",
    };

    public static string Of(string status) => Labels.GetValueOrDefault(status, status);
}
