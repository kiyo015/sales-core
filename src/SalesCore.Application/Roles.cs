namespace SalesCore.Application;

/// <summary>
/// 役割(業務ルール集「8. 利用者とログイン」の「役割」、2026-10-26 決定)。名前は AspNetRoles.Name にそのまま入る。
/// 1人に複数付けられ、できる操作は付いた役割の合計。役割ごとにできる操作は業務ルール集の表が唯一の定義元。
/// </summary>
public static class Roles
{
    public const string Sales = "営業";
    public const string Accounting = "経理";
    public const string Admin = "管理者";

    public static IReadOnlyList<string> All { get; } = [Sales, Accounting, Admin];
}
