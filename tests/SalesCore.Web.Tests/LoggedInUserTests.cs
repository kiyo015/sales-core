using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using SalesCore.Application;

namespace SalesCore.Web.Tests;

/// <summary>
/// 操作した人の出どころ。画面の対話中(回線の中)は Blazor の認証の状態から、それ以外(静的な描画・API)は HTTP の利用者から読む。
/// Blazor の回線の中では HttpContext を当てにできないため(ASP.NET Core の文書)。
/// </summary>
public class LoggedInUserTests
{
    [Fact]
    public void 画面の対話中は_認証の状態の利用者のIdを返す()
    {
        var user = new LoggedInUser(new HttpContextAccessor(), new FixedAuthentication(Principal("user-a")));

        Assert.Equal("user-a", user.UserId);
    }

    [Fact]
    public void 認証の状態がまだ無い時は_HTTPの利用者のIdを返す()
    {
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = Principal("user-b") } };

        Assert.Equal("user-b", new LoggedInUser(http, new NotYetSetAuthentication()).UserId);
        Assert.Equal("user-b", new LoggedInUser(http).UserId); // API(Blazor の認証の状態が無い)
    }

    // 回線の中でログアウト扱いになった時に、回線を張った時の HttpContext の古い利用者が入らないこと(PR #10 の AIレビュー)
    [Fact]
    public void Blazorの認証の状態が取れて未ログインなら_HTTPの利用者に戻らない()
    {
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = Principal("stale-user") } };
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.Null(new LoggedInUser(http, new FixedAuthentication(anonymous)).UserId);
    }

    [Fact]
    public void ログインしていなければ_null()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = anonymous } };

        Assert.Null(new LoggedInUser(http, new FixedAuthentication(anonymous)).UserId);
        Assert.Null(new LoggedInUser(new HttpContextAccessor()).UserId);
    }

    private static ClaimsPrincipal Principal(string id) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id), new Claim(ClaimTypes.Name, "name-" + id)], "test"));

    private sealed class FixedAuthentication(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
    }

    // 起動時の処理など、画面の外では認証の状態が設定されていない(ServerAuthenticationStateProvider は例外を投げる)
    private sealed class NotYetSetAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            throw new InvalidOperationException("GetAuthenticationStateAsync was called before SetAuthenticationState.");
    }
}
