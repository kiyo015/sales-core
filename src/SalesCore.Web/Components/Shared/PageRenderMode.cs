using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace SalesCore.Web.Components.Shared;

/// <summary>ユースケースを呼ぶ画面の描画方式(<c>@rendermode PageRenderMode.Interactive</c>)。</summary>
public static class PageRenderMode
{
    /// <summary>
    /// Interactive Server で、先に静的に描かない(プリレンダーしない)。先に描くと、接続が繋がるまでの間に入れた値や押したボタンが
    /// 描き直しで失われ(2026-11-06 の端から端までのテストで確認)、画面の読み込み(ユースケースの呼び出し)も2回走る。
    /// 接続が繋がってから中身を出す。
    /// </summary>
    public static IComponentRenderMode Interactive { get; } = new InteractiveServerRenderMode(prerender: false);
}
