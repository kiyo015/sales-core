using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using SalesCore.Web.Components.Pages;

namespace SalesCore.Web.Tests;

/// <summary>
/// ユースケースを呼ぶ画面(<see cref="Components.Shared.OperationPage"/> を継ぐもの)は Interactive Server で描く。
/// 静的な描画のままだと、ボタンを押しても処理が動かない。bUnit は描画方式を見ないので、ここで止める
/// (2026-10-30 に書き出した、どのテストも通らない経路)。
/// </summary>
public class RenderModeTests
{
    private static IEnumerable<Type> OperationPages() =>
        typeof(Home).Assembly.GetTypes().Where(type => type.IsSubclassOf(typeof(Components.Shared.OperationPage)) && !type.IsAbstract);

    [Fact]
    public void ユースケースを呼ぶ画面は_InteractiveServerで描く()
    {
        var notInteractive = OperationPages()
            .Where(page => page.GetCustomAttribute<RenderModeAttribute>(inherit: true)?.Mode is not InteractiveServerRenderMode)
            .Select(page => page.Name)
            .ToList();

        Assert.Empty(notInteractive);
    }

    // 調べ方の確認。マスタの画面が見つからなければ、画面の探し方が誤っている
    [Fact]
    public void 調べ方の確認_マスタの画面が見つかる()
    {
        Assert.Equal(["Customers", "Products", "Warehouses"], OperationPages().Select(p => p.Name).Order());
    }
}
