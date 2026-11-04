namespace SalesCore.Web.Components.Pages.Orders;

/// <summary>出荷の状態の表示名(業務ルール集「3. 販売の流れ」の名前)。ユースケースは英語の名前(Instructed など)で返す。</summary>
public static class ShipmentStatusText
{
    // 状態を足したら、ここにも足す(ShipmentPageTests が止める)
    public static IReadOnlyDictionary<string, string> Labels { get; } = new Dictionary<string, string>
    {
        ["Instructed"] = "指示",
        ["Confirmed"] = "確定",
        ["Cancelled"] = "取消",
    };

    public static string Of(string status) => Labels.GetValueOrDefault(status, status);
}
