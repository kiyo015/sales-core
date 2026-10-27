using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using SalesCore.Web.Components.Account;

namespace SalesCore.Web.Tests;

/// <summary>対話的な画面遷移で未ログインなら、元の画面を戻り先にしてログインの画面へ送る(PR #10 の AIレビューの Minor)。</summary>
public class RedirectToLoginTests : BunitContext
{
    [Fact]
    public void ログインの画面へ_元の画面を戻り先にして送る()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/sales-orders/1?tab=lines");

        Render<RedirectToLogin>();

        Assert.Equal("http://localhost/account/login?ReturnUrl=%2Fsales-orders%2F1%3Ftab%3Dlines", navigation.Uri);
    }
}
