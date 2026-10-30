using Microsoft.Extensions.DependencyInjection;
using SalesCore.Application;
using SalesCore.Web.Components.Layout;
using SalesCore.Web.Components.Shared;

namespace SalesCore.Web.Tests;

/// <summary>
/// 画面の共通部品。メニューは役割に応じて出し分け(入口の検査と同じ表 Permissions から決める)、
/// 誤りは状態コードごとの見出しと理由を出す(API と同じ扱い)。
/// </summary>
public class SharedComponentTests : BunitContext
{
    [Theory]
    [InlineData(Roles.Sales)]
    [InlineData(Roles.Accounting)]
    [InlineData(Roles.Admin)]
    public void マスタを参照できる役割には_マスタのメニューが出る(string role)
    {
        Services.AddSingleton<ICurrentUser>(new FakeUser(role));

        var menu = Render<NavMenu>();

        Assert.Equal(["トップ", "得意先", "商品", "倉庫"], menu.FindAll("a").Select(a => a.TextContent));
    }

    [Fact]
    public void 役割の無い利用者には_トップだけが出る()
    {
        Services.AddSingleton<ICurrentUser>(new FakeUser());

        var menu = Render<NavMenu>();

        Assert.Equal(["トップ"], menu.FindAll("a").Select(a => a.TextContent));
    }

    [Theory]
    [InlineData(400, "入力を確かめてください。")]
    [InlineData(403, "この操作をする権限がありません。")]
    [InlineData(404, "見つかりません。")]
    [InlineData(409, "今の状態ではできません。")]
    [InlineData(500, "問題が起きました。")]
    public void 誤りは_状態ごとの見出しと理由を出す(int status, string heading)
    {
        var message = Render<ErrorMessage>(p => p.Add(m => m.Error, new OperationError(status, "理由の文言")));

        var alert = message.Find("[role=alert]");
        Assert.Equal(heading, alert.QuerySelector("strong")!.TextContent);
        Assert.Equal("理由の文言", alert.QuerySelector("p")!.TextContent);
    }

    [Fact]
    public void 誤りが無ければ_何も出さない()
    {
        var message = Render<ErrorMessage>(p => p.Add(m => m.Error, null));

        Assert.Empty(message.FindAll("[role=alert]"));
    }

    private sealed class FakeUser(params string[] roles) : ICurrentUser
    {
        public string? UserId => "fake-user";

        public bool IsAuthenticated => true;

        public bool IsInRole(string role) => roles.Contains(role);
    }
}
