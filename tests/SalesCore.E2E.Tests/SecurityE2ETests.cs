using System.Net;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;
using static SalesCore.E2E.Tests.E2EScreens;

namespace SalesCore.E2E.Tests;

/// <summary>
/// セキュリティの確認(Day47)。発行した画面に、本物のブラウザと素の HTTP で確かめる。
/// 確かめた項目と結果の表は、Day47 の PR と学習記録(2026-11-10)にある。
/// </summary>
[Collection(WebCollection.Name)]
public sealed class SecurityE2ETests(WebAppFixture app)
{
    private const string AuthCookie = ".AspNetCore.Identity.Application";

    [Fact]
    public async Task ログインのCookieは_スクリプトから読めず_httpsでしか送られず_印のCookieは他のサイトから送られない()
    {
        var data = await E2EData.SeedAsync(app);
        await using var context = await app.Browser.NewContextAsync(new() { BaseURL = app.BaseUrl });
        await LogInAsync(await context.NewPageAsync(), data.Sales);

        var cookies = await context.CookiesAsync();
        var auth = cookies.Single(c => c.Name == AuthCookie);
        Assert.True(auth.HttpOnly);
        Assert.True(auth.Secure);
        Assert.Equal(SameSiteAttribute.Lax, auth.SameSite); // 他のサイトからの POST には付かない
        Assert.Equal(-1, auth.Expires); // ブラウザを閉じれば消える(ログインしたままにしない)
        var antiforgery = cookies.Single(c => c.Name.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal));
        // 印の Cookie は Secure を強制できない(Always にすると、印の仕組みが https でない要求を例外で拒み、http の手元で画面が開かない。
        // 2026-11-10 実測)。印だけではログインの代わりにならないので、要求に合わせる既定のままにする
        Assert.True(antiforgery.HttpOnly);
        Assert.Equal(SameSiteAttribute.Strict, antiforgery.SameSite);
    }

    [Fact]
    public async Task 印の無いログインとログアウトの送信は_400で拒み_ログインの状態を変えない()
    {
        var data = await E2EData.SeedAsync(app);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = new Uri(app.BaseUrl) };

        var login = await http.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "login", ["Input.UserName"] = data.Sales.UserName, ["Input.Password"] = data.Sales.Password,
        }));
        Assert.Equal(HttpStatusCode.BadRequest, login.StatusCode);
        Assert.False(login.Headers.Contains("Set-Cookie"));

        // ログインした人の Cookie を持った、他のサイトからのログアウトの送信(印が無い)
        await using var context = await app.Browser.NewContextAsync(new() { BaseURL = app.BaseUrl });
        await LogInAsync(await context.NewPageAsync(), data.Sales);
        var auth = (await context.CookiesAsync()).Single(c => c.Name == AuthCookie);
        var logout = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/account/logout")
        {
            Headers = { { "Cookie", $"{auth.Name}={auth.Value}" } },
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["returnUrl"] = "/" }),
        });
        Assert.Equal(HttpStatusCode.BadRequest, logout.StatusCode);
        // ログアウトはブラウザに Cookie を消させる応答(値が空の Set-Cookie)で行う。それを返さないこと
        // (手元に残した Cookie の値は、ログアウトしても通るので、それでは確かめられない。PR #20 の AIレビュー)。
        // 値のある Set-Cookie は返りうる: ここでは確かめ直す間隔が1秒なので、ログインから1秒を超えると、
        // 拒む前の認証の段階で Cookie が作り直される(2026-11-11、全体を9回流して2回落ちて分かった。2.5秒待たせると毎回付く)
        var setCookies = logout.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];
        Assert.DoesNotContain(setCookies, c => c.StartsWith(AuthCookie + "=;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 画面は他のサイトの枠に入れさせず_エラーの画面は問い合わせ番号だけを出す()
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(app.BaseUrl) };

        var login = await http.GetAsync("/account/login");
        Assert.Equal(["SAMEORIGIN"], login.Headers.GetValues("X-Frame-Options"));

        var error = await (await http.GetAsync("/Error")).Content.ReadAsStringAsync();
        Assert.Contains("問い合わせ番号", error);
        Assert.DoesNotContain("Exception", error);
        Assert.DoesNotContain(" at ", error); // 例外の呼び出し履歴
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/no-such-page")).StatusCode);
    }

    [Fact]
    public async Task 画面で5回続けて間違えると_正しいパスワードでもロック中と出る()
    {
        var data = await E2EData.SeedAsync(app);
        await using var context = await app.Browser.NewContextAsync(new() { BaseURL = app.BaseUrl });
        var page = await context.NewPageAsync();

        for (var i = 1; i <= 5; i++)
        {
            await TryLogInAsync(page, data.Sales.UserName, $"wrong password {i}");
        }
        await TryLogInAsync(page, data.Sales.UserName, data.Sales.Password);

        await Expect(page.GetByRole(AriaRole.Alert)).ToHaveTextAsync("続けて間違えたため、ロックしています。15分たってからやり直してください。");
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "ログアウト" })).ToHaveCountAsync(0);
    }

    private static async Task TryLogInAsync(IPage page, string userName, string password)
    {
        await page.GotoAsync("/account/login");
        await page.GetByLabel("利用者名").FillAsync(userName);
        await page.GetByLabel("パスワード").FillAsync(password);
        await page.GetByRole(AriaRole.Button, new() { Name = "ログイン" }).ClickAsync();
        await page.GetByRole(AriaRole.Alert).WaitForAsync();
    }
}
