using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SalesCore.E2E.Tests;

/// <summary>端から端までのテストで繰り返す画面の操作。</summary>
public static class E2EScreens
{
    public static async Task LogInAsync(IPage page, E2EUser user)
    {
        await page.GotoAsync("/account/login");
        await page.GetByLabel("利用者名").FillAsync(user.UserName);
        await page.GetByLabel("パスワード").FillAsync(user.Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "ログイン" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "ログアウト" })).ToBeVisibleAsync();
    }

    /// <summary>受注の登録の画面で受注を登録し、受注の画面に移ったところで受注の ID を返す。</summary>
    public static async Task<long> RegisterOrderAsync(IPage page, string customer, string orderedOn, params (string Product, string Quantity)[] lines)
    {
        await page.GotoAsync("/orders/new");
        await page.Locator($"#customer option[value='{customer}']").WaitForAsync(new() { State = WaitForSelectorState.Attached });
        await page.SelectOptionAsync("#customer", customer);
        await page.FillAsync("#ordered-on", orderedOn);
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) await page.ClickAsync("button.add-line");
            await page.SelectOptionAsync($"#product-{i}", lines[i].Product);
            await page.FillAsync($"#quantity-{i}", lines[i].Quantity);
        }
        await page.GetByRole(AriaRole.Button, new() { Name = "登録" }).ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/orders/\d+$"));
        return long.Parse(Regex.Match(page.Url, @"/orders/(\d+)$").Groups[1].Value);
    }

    public static async Task SaveScreenshotsAsync(string name, params IPage[] pages)
    {
        for (var i = 0; i < pages.Length; i++)
        {
            await pages[i].ScreenshotAsync(new() { Path = Path.Combine(AppContext.BaseDirectory, $"e2e-failure-{name}-{i}.png"), FullPage = true });
        }
    }
}

/// <summary>
/// 画面の部品(CSS・スクリプト)や画面そのものの読み込みの失敗。操作が通っても、見た目が崩れていれば気付けるようにする。
/// 応答のイベントはブラウザとのやり取りの中で別のスレッドから来るので、一覧はロックの中で書き、読む。
/// </summary>
public sealed class RequestFailures
{
    private readonly List<string> _items = [];

    public void Watch(IPage page)
    {
        page.Response += (_, response) => { if (response.Status >= 400) Add($"{response.Status} {response.Url}"); };
        // 打ち切り(ERR_ABORTED)は除く。画面を移る時に、前の画面の読み込みが打ち切られるのはブラウザの普通の動き
        page.RequestFailed += (_, request) =>
        {
            if (request.Failure?.Contains("ERR_ABORTED") == true) return;
            Add($"失敗 {request.Url} {request.Failure}");
        };
    }

    public void AssertNone()
    {
        lock (_items)
        {
            Assert.True(_items.Count == 0, string.Join(Environment.NewLine, _items));
        }
    }

    private void Add(string item)
    {
        lock (_items)
        {
            _items.Add(item);
        }
    }
}
