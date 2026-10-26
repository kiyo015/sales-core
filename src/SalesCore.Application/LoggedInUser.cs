using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using SalesCore.Infrastructure.Persistence;

namespace SalesCore.Application;

/// <summary>
/// 操作した人 = ログインした利用者の Id(ClaimTypes.NameIdentifier。Identity は AspNetUsers.Id を入れる)。
/// 業務の DbContext が保存の時に読む(<see cref="ICurrentUserSource"/>)。ログインしていなければ null。
/// </summary>
/// <remarks>
/// 画面の対話中(Blazor の回線の中)は HttpContext を当てにできないので、Blazor の認証の状態を先に見る。
/// 認証の状態が無い(API)か、まだ設定されていない(起動時の処理など、画面の外)時は、HTTP の利用者を見る。
/// </remarks>
public sealed class LoggedInUser(IHttpContextAccessor http, AuthenticationStateProvider? authentication = null) : ICurrentUserSource
{
    // Blazor の認証の状態が取れたら、それだけを使う。未ログインでも HTTP の利用者には戻らない
    // (回線の中でログアウト扱いになった時に、回線を張った時の古い利用者が入らないように。PR #10 の AIレビュー)
    public string? UserId => FromBlazor() is { } blazor ? IdOf(blazor) : IdOf(http.HttpContext?.User);

    private ClaimsPrincipal? FromBlazor()
    {
        if (authentication is null) return null;
        try
        {
            // サーバー側の認証の状態は、設定済みなら完了した Task で返る。完了していなければ待たずに HTTP の利用者を見る
            var state = authentication.GetAuthenticationStateAsync();
            return state.IsCompletedSuccessfully ? state.Result.User : null;
        }
        catch (InvalidOperationException)
        {
            return null; // まだ設定されていない(ServerAuthenticationStateProvider が投げる)
        }
    }

    private static string? IdOf(ClaimsPrincipal? user) =>
        user?.Identity?.IsAuthenticated == true ? user.FindFirstValue(ClaimTypes.NameIdentifier) : null;
}
