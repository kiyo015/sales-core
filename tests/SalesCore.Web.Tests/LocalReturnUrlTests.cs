using SalesCore.Web.Components.Pages.Account;

namespace SalesCore.Web.Tests;

/// <summary>
/// ログインの後の戻り先(?ReturnUrl=)は、このサイトの中だけ。外のサイトへ飛ばす罠のリンク(オープンリダイレクト)を防ぐ。
/// 手書きの判定なので、ブラウザが外のサイトとして解釈する書き方を並べて確かめる(PR #9 の AIレビューの指摘)。
/// </summary>
public class LocalReturnUrlTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/sales-orders/1")]
    [InlineData("/sales-orders?page=2")]
    public void サイトの中の戻り先は_そのまま使う(string url)
    {
        Assert.Equal(url, LocalReturnUrl.OrRoot(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://evil.example")]
    [InlineData("evil.example")]
    [InlineData("//evil.example")]       // スキームを省いた外のサイト
    [InlineData("/\\evil.example")]      // ブラウザは \ を / と読み替える
    [InlineData("/\t/evil.example")]     // ブラウザはタブを取り除くので //evil.example になる
    [InlineData("/\n/evil.example")]
    public void 外のサイトや空の戻り先は_トップページにする(string? url)
    {
        Assert.Equal("/", LocalReturnUrl.OrRoot(url));
    }
}
