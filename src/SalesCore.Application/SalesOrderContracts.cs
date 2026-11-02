using SalesCore.Domain;

namespace SalesCore.Application;

// 受注・出荷の API の入力と出力。金額は円、税率は % の数値。状態は英語の名前(Received など)。

/// <summary>受注の登録。単価を省くと商品の標準単価を使う。税率は受注日時点の、商品の税区分の税率。</summary>
public sealed record CreateSalesOrder(string CustomerCode, DateOnly OrderedOn, IReadOnlyList<CreateSalesOrderLine>? Lines);

public sealed record CreateSalesOrderLine(string ProductCode, decimal Quantity, decimal? UnitPrice = null);

/// <summary>出荷の指示。どの受注明細をいくつ、どの倉庫から出すか。</summary>
public sealed record InstructShipment(string WarehouseCode, IReadOnlyList<ShipmentLineRequest>? Lines);

public sealed record ShipmentLineRequest(long OrderLineId, decimal Quantity);

/// <summary>出荷の確定。出荷日が売上日になる。</summary>
public sealed record ConfirmShipment(DateOnly ShippedOn);

public sealed record SalesOrderView(
    long Id, string Number, string Status, string CustomerCode, DateOnly OrderedOn, decimal Amount, IReadOnlyList<SalesOrderLineView> Lines);

/// <summary>受注の一覧の1行。金額は税抜合計。</summary>
public sealed record SalesOrderSummary(long Id, string Number, string Status, string CustomerCode, DateOnly OrderedOn, decimal Amount);

/// <summary>
/// 画面が受注の承認・取消のボタンを出すか。状態は Domain(<see cref="SalesOrderStatusRules"/>)、役割は <see cref="Permissions"/> と同じ決まりで見る。
/// 隠すのは使い勝手のためで、守りはユースケースの入口(ボタンを出した後に状態が変われば、押しても 409)。
/// </summary>
public static class SalesOrderActions
{
    public static bool CanApprove(ICurrentUser user, SalesOrderView order)
    {
        var status = Enum.Parse<SalesOrderStatus>(order.Status);
        return status.CanApprove() && Permissions.Allows(user, Operation.ApproveSalesOrder)
            && (status is not SalesOrderStatus.PendingApproval || Permissions.Allows(user, Operation.ApprovePendingSalesOrder));
    }

    public static bool CanCancel(ICurrentUser user, SalesOrderView order) =>
        Enum.Parse<SalesOrderStatus>(order.Status).CanCancel() && Permissions.Allows(user, Operation.CancelSalesOrder);
}

public sealed record SalesOrderLineView(
    long Id, string ProductCode, decimal Quantity, decimal UnitPrice, decimal TaxRate, decimal ShippedQuantity, decimal RemainingQuantity);

/// <summary>出荷。確定した出荷には、計上した売上が付く。</summary>
public sealed record ShipmentView(
    long Id, string Number, string Status, long SalesOrderId, DateOnly? ShippedOn, IReadOnlyList<ShipmentLineView> Lines, SalesRecordView? SalesRecord);

/// <summary>出荷の明細(出力)。入力の <see cref="ShipmentLineRequest"/> と同じ形だが、別の型にする(入力を変えても出力が変わらないように。Day30 からの持ち越し)。</summary>
public sealed record ShipmentLineView(long OrderLineId, decimal Quantity);

public sealed record SalesRecordView(long Id, string Number, DateOnly RecordedOn, decimal Amount);

/// <summary>指定した受注・出荷が無い。API は 404 にする。</summary>
public sealed class NotFoundException(string message) : Exception(message);
