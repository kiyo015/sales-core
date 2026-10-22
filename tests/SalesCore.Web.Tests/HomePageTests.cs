using SalesCore.Web.Components.Pages;

namespace SalesCore.Web.Tests;

/// <summary>画面の部品を、ブラウザなしで描画して確かめる(bUnit)。</summary>
public class HomePageTests : BunitContext
{
    [Fact]
    public void トップページにシステム名の見出しが出る()
    {
        var page = Render<Home>();

        Assert.Equal("販売管理", page.Find("h1").TextContent);
    }
}
