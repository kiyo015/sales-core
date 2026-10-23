using Microsoft.AspNetCore.Identity;

namespace SalesCore.Infrastructure.Persistence.Identity;

/// <summary>
/// このシステムの利用者(ASP.NET Core Identity の利用者に、表示名と有効・無効を足したもの)。
/// パスワードは Identity の標準のハッシュ化で <see cref="IdentityUser.PasswordHash"/> に入り、平文では持たない。
/// </summary>
public sealed class SalesCoreUser : IdentityUser
{
    /// <summary>画面に出す名前(例: 山田 太郎)。ログインには使わない。</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// false なら、正しいパスワードでもログインできない(業務ルール集「8. 利用者とログイン」)。
    /// 利用者も物理削除せず、ここを false にして無効にする。
    /// </summary>
    public bool IsActive { get; set; } = true;
}
