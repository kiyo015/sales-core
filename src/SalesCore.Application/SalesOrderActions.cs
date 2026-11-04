using SalesCore.Domain;

namespace SalesCore.Application;

/// <summary>
/// 画面が受注・出荷のボタンを出すか。状態は Domain(<see cref="SalesOrderStatusRules"/>・<see cref="ShipmentStatusRules"/>)、
/// 役割は <see cref="Permissions"/> と同じ決まりで見る。
/// 隠すのは使い勝手のためで、守りはユースケースの入口(ボタンを出した後に状態が変われば、押しても 409)。
/// </summary>
public static class SalesOrderActions
{
    public static bool CanApprove(ICurrentUser user, SalesOrderView order)
    {
        var status = StatusOf(order);
        return status.CanApprove() && Permissions.Allows(user, Operation.ApproveSalesOrder)
            && (status is not SalesOrderStatus.PendingApproval || Permissions.Allows(user, Operation.ApprovePendingSalesOrder));
    }

    public static bool CanCancel(ICurrentUser user, SalesOrderView order) =>
        StatusOf(order).CanCancel() && Permissions.Allows(user, Operation.CancelSalesOrder);

    public static bool CanInstructShipment(ICurrentUser user, SalesOrderView order) =>
        StatusOf(order).CanShip() && Permissions.Allows(user, Operation.InstructShipment);

    /// <summary>確定は、出荷が指示の段階で、受注も出荷できる状態の時(取り消した受注の指示は確定できない)。</summary>
    public static bool CanConfirmShipment(ICurrentUser user, SalesOrderView order, ShipmentView shipment) =>
        StatusOf(shipment).CanConfirm() && StatusOf(order).CanShip() && Permissions.Allows(user, Operation.ConfirmShipment);

    /// <summary>取消は、出荷が指示の段階なら受注の状態によらない(取り消した受注に残った指示も片付けられる)。</summary>
    public static bool CanCancelShipment(ICurrentUser user, ShipmentView shipment) =>
        StatusOf(shipment).CanCancel() && Permissions.Allows(user, Operation.CancelShipment);

    private static SalesOrderStatus StatusOf(SalesOrderView order) => Enum.Parse<SalesOrderStatus>(order.Status);

    private static ShipmentStatus StatusOf(ShipmentView shipment) => Enum.Parse<ShipmentStatus>(shipment.Status);
}
