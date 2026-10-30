namespace SalesCore.Application;

/// <summary>ユースケースの操作。どの役割ができるかは <see cref="Permissions"/>。</summary>
public enum Operation
{
    ViewSalesOrder,
    RegisterSalesOrder,
    CancelSalesOrder,
    ApproveSalesOrder,
    ApprovePendingSalesOrder,
    InstructShipment,
    ConfirmShipment,
    CancelShipment,
    ViewShipment,
    CloseInvoice,
    ViewInvoice,
    ViewMasters,
    RegisterMaster,
}

/// <summary>ログインしていない利用者の操作(API は 401)。</summary>
public sealed class UnauthenticatedException() : Exception("ログインしていない。");

/// <summary>役割が足りない操作(API は 403)。</summary>
public sealed class ForbiddenException(string message) : Exception(message);

/// <summary>
/// 役割ごとにできる操作(業務ルール集「8.」の「役割」の表)。ユースケースの入口で <see cref="Require"/> を呼ぶ。
/// 画面と API の両方がユースケースを通るので、ここで止めれば両方の経路に効く。
/// </summary>
public static class Permissions
{
    private static readonly string[] Everyone = [Roles.Sales, Roles.Accounting, Roles.Admin];

    // 業務ルール集の表と同じ内容。PermissionTests が表を読んで、ここと食い違えば落ちる
    private static readonly Dictionary<Operation, string[]> Allowed = new()
    {
        // 受注・出荷・請求書の参照
        [Operation.ViewSalesOrder] = Everyone,
        [Operation.ViewShipment] = Everyone,
        [Operation.ViewInvoice] = Everyone,
        // 受注の登録・取消、受注の承認(税抜合計100万円未満 = 受付の受注)
        [Operation.RegisterSalesOrder] = [Roles.Sales, Roles.Admin],
        [Operation.CancelSalesOrder] = [Roles.Sales, Roles.Admin],
        [Operation.ApproveSalesOrder] = [Roles.Sales, Roles.Admin],
        // 受注の承認(税抜合計100万円以上 = 承認待ちの受注。100万円は基準額の既定値)
        [Operation.ApprovePendingSalesOrder] = [Roles.Admin],
        // 出荷の指示・確定・取消
        [Operation.InstructShipment] = [Roles.Sales, Roles.Admin],
        [Operation.ConfirmShipment] = [Roles.Sales, Roles.Admin],
        [Operation.CancelShipment] = [Roles.Sales, Roles.Admin],
        // 締め請求
        [Operation.CloseInvoice] = [Roles.Accounting, Roles.Admin],
        // マスタ(得意先・商品・倉庫)の参照と、登録・変更
        [Operation.ViewMasters] = Everyone,
        [Operation.RegisterMaster] = [Roles.Admin],
    };

    // 表に無い操作は管理者だけ(書き忘れた時に、拒む側に倒す。業務ルール集「役割」)
    private static readonly string[] Default = [Roles.Admin];

    /// <summary>ログインしていなければ <see cref="UnauthenticatedException"/>、役割が足りなければ <see cref="ForbiddenException"/>。</summary>
    public static void Require(ICurrentUser user, Operation operation)
    {
        if (!user.IsAuthenticated) throw new UnauthenticatedException();
        var allowed = Allowed.GetValueOrDefault(operation, Default);
        if (!Allows(user, operation))
        {
            throw new ForbiddenException($"この操作({operation})は {string.Join("・", allowed)} だけができる。");
        }
    }

    /// <summary>
    /// 操作できるか。画面がメニューやボタンを出すかを決めるのに使う(使い勝手のため。守りは <see cref="Require"/>)。
    /// 同じ表から決めるので、出したボタンが入口で拒まれることはない。
    /// </summary>
    public static bool Allows(ICurrentUser user, Operation operation) =>
        user.IsAuthenticated && Allowed.GetValueOrDefault(operation, Default).Any(user.IsInRole);
}
