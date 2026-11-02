namespace SalesCore.Web.Components.Pages.Orders;

/// <summary>受注の状態の表示名(業務ルール集「3. 販売の流れ」の名前)。ユースケースは英語の名前(Received など)で返す。</summary>
public static class OrderStatusText
{
    // 状態を足したら、ここにも足す(SalesOrderPageTests が止める)
    public static IReadOnlyDictionary<string, string> Labels { get; } = new Dictionary<string, string>
    {
        ["Received"] = "受付",
        ["PendingApproval"] = "承認待ち",
        ["Approved"] = "承認済",
        ["PartiallyShipped"] = "一部出荷",
        ["Shipped"] = "出荷済",
        ["Cancelled"] = "取消",
    };

    public static string Of(string status) => Labels.GetValueOrDefault(status, status);
}
