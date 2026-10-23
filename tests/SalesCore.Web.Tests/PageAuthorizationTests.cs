using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using SalesCore.Web.Components.Pages;

namespace SalesCore.Web.Tests;

/// <summary>
/// 画面はログインしていなければ開けない(業務ルール集「8. 利用者とログイン」)。
/// すべての画面に [Authorize] を付け(_Imports.razor)、ログインしなくても開ける画面は決めたものだけにする。
/// 画面を足した時に付け忘れても、ここで止まる。
/// </summary>
public class PageAuthorizationTests
{
    // ログインしなくても開ける画面。足す時は、なぜ要るかをここに書く
    private static readonly string[] AnonymousPages =
    [
        "/account/login", // ログインの画面そのもの
        "/Error",         // 例外の時の画面(ログイン前にも出る)
    ];

    private static IEnumerable<(string Route, Type Page)> Pages() =>
        typeof(Home).Assembly.GetTypes()
            .SelectMany(type => type.GetCustomAttributes<RouteAttribute>().Select(route => (route.Template, type)));

    [Fact]
    public void 画面は_決めたもの以外すべてログインを求める()
    {
        var anonymous = Pages()
            .Where(p => p.Page.GetCustomAttribute<AllowAnonymousAttribute>() is not null
                || p.Page.GetCustomAttribute<AuthorizeAttribute>() is null)
            .Select(p => p.Route)
            .Order()
            .ToList();

        Assert.Equal(AnonymousPages.Order(), anonymous);
    }

    // 調べる仕組みが働いていることの確認。トップページが見つからなければ、画面の探し方が誤っている
    [Fact]
    public void 調べ方の確認_トップページが見つかる()
    {
        Assert.Contains(Pages(), p => p.Route == "/" && p.Page == typeof(Home));
    }
}
