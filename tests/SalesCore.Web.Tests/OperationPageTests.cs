using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SalesCore.Web.Components.Shared;

namespace SalesCore.Web.Tests;

/// <summary>
/// ログインしていない扱いに変わった画面(役割を外した・無効にした。業務ルール集「8.」)は、読み込みを始めずにログインの画面へ送る
/// (PR #20 の AIレビューの Minor: 送る前に読み込みが走り、無駄な問い合わせや一瞬の誤りの表示が出た)。
/// </summary>
public class OperationPageTests : BunitContext
{
    [Fact]
    public void ログインしていなければ_読み込みを始めずにログインの画面へ送る()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/masters/warehouses");

        var page = Render<LoadingPage>(p => p.AddCascadingValue(StateOf(new ClaimsIdentity())));

        Assert.False(page.Instance.Loaded);
        Assert.Equal("http://localhost/account/login?ReturnUrl=%2Fmasters%2Fwarehouses", navigation.Uri);
    }

    // 対照: ログインしていれば読み込み、どこへも送らない
    [Fact]
    public void ログインしていれば_読み込む()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/masters/warehouses");

        var page = Render<LoadingPage>(p => p.AddCascadingValue(StateOf(new ClaimsIdentity("test"))));

        Assert.True(page.Instance.Loaded);
        Assert.Equal("http://localhost/masters/warehouses", navigation.Uri);
    }

    private static Task<AuthenticationState> StateOf(ClaimsIdentity identity) =>
        Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));

    private sealed class LoadingPage : OperationPage
    {
        public bool Loaded { get; private set; }

        protected override Task OnInitializedAsync()
        {
            Loaded = true; // 本物の画面ならユースケースを呼ぶ所
            return Task.CompletedTask;
        }
    }
}
