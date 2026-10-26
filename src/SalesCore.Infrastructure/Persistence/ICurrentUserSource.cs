namespace SalesCore.Infrastructure.Persistence;

/// <summary>
/// 操作した人(ログインした利用者の Id = AspNetUsers.Id)の出どころ。業務の DbContext が保存の時に読み、
/// 作成者・更新者・監査ログの user_id に入れる(業務ルール集「8. 利用者とログイン」の「役割」)。
/// 実装は Application にある(画面はログイン中の利用者、API は HTTP の利用者)。テストでは差し替える。
/// </summary>
public interface ICurrentUserSource
{
    /// <summary>ログインしていなければ null。</summary>
    string? UserId { get; }
}
