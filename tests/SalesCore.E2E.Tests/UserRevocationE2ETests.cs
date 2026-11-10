using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using SalesCore.Application;
using SalesCore.Infrastructure.Persistence.Identity;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;
using static SalesCore.E2E.Tests.E2EScreens;

namespace SalesCore.E2E.Tests;

/// <summary>
/// ログインしている人の役割を外したり、無効にしたりした時に、画面でも効くこと(業務ルール集「8. 利用者とログイン」。本人の決定 2026-11-10: 1分以内)。
/// 開いたままの画面(回線)と、開き直した画面(Cookie)の両方を確かめる。
/// 確かめ直す間隔は設定 SalesCore:UserRevalidationInterval(既定1分)で、ここでは画面を2秒で起動している(<see cref="WebAppFixture"/>)。
/// <para>
/// 2026-11-10 の実測: 確かめ直す前は、管理者の役割を外しても、開いたままの画面で倉庫を登録でき、開き直しても登録の欄が出た。
/// 無効にした後も画面を使えた(Cookie の有効期限まで、最長14日)。
/// </para>
/// </summary>
[Collection(WebCollection.Name)]
public sealed class UserRevocationE2ETests(WebAppFixture app, ITestOutputHelper output)
{
    private static readonly LocatorAssertionsToHaveCountOptions Revalidated = new() { Timeout = 15_000 };

    [Fact]
    public async Task 管理者の役割を外すと_開いたままの画面はログインに戻り_開き直すと新しい役割で拒まれる()
    {
        var data = await E2EData.SeedAsync(app);
        await using var context = await app.Browser.NewContextAsync(new() { BaseURL = app.BaseUrl });
        var page = await context.NewPageAsync();
        try
        {
            await LogInAsync(page, data.Admin);
            await page.GotoAsync("/masters/warehouses");
            await Expect(page.Locator("legend", new() { HasText = "倉庫の登録" })).ToBeVisibleAsync();

            await ChangeUserAsync(data.Admin, (users, user) => users.RemoveFromRoleAsync(user, Roles.Admin));

            // 開いたままの画面: 登録の欄が消え、ログインの画面へ送られる
            await Expect(page.Locator("legend", new() { HasText = "倉庫の登録" })).ToHaveCountAsync(0, Revalidated);
            await Expect(page).ToHaveURLAsync(new Regex("/account/login"));
            // 開き直した画面: Cookie は確かめ直した時に DB の役割で作り直される。ログインしたままで、役割が1つも無いので参照も拒まれる
            await page.GotoAsync("/masters/warehouses");
            await Expect(page.Locator(".operation-error[data-status='403']")).ToContainTextAsync("ViewMasters");
            await Expect(page.Locator("legend", new() { HasText = "倉庫の登録" })).ToHaveCountAsync(0);
        }
        catch
        {
            await SaveScreenshotsAsync("revoke-role", page);
            throw;
        }
        finally
        {
            output.WriteLine(app.WebLog);
        }
    }

    [Fact]
    public async Task 無効にすると_開いたままの画面の登録の欄が消え_開き直してもログインに戻る()
    {
        var data = await E2EData.SeedAsync(app);
        await using var context = await app.Browser.NewContextAsync(new() { BaseURL = app.BaseUrl });
        var page = await context.NewPageAsync();
        try
        {
            await LogInAsync(page, data.Admin);
            await page.GotoAsync("/masters/warehouses");
            await Expect(page.Locator("legend", new() { HasText = "倉庫の登録" })).ToBeVisibleAsync();

            await ChangeUserAsync(data.Admin, (users, user) =>
            {
                user.IsActive = false;
                return users.UpdateAsync(user);
            });

            await Expect(page.Locator("legend", new() { HasText = "倉庫の登録" })).ToHaveCountAsync(0, Revalidated);
            // 欄が消えた後、画面はログインの画面へ移る。移り終わる前に開き直すと、二つの移動がぶつかる(2026-11-10 に1回落ちた)
            await Expect(page).ToHaveURLAsync(new Regex("/account/login"));
            await page.GotoAsync("/masters/warehouses");
            await Expect(page).ToHaveURLAsync(new Regex("/account/login"));
        }
        catch
        {
            await SaveScreenshotsAsync("deactivate", page);
            throw;
        }
        finally
        {
            output.WriteLine(app.WebLog);
        }
    }

    private async Task ChangeUserAsync(E2EUser target, Func<UserManager<SalesCoreUser>, SalesCoreUser, Task<IdentityResult>> change)
    {
        await using var provider = E2EData.IdentityServices(app);
        await using var scope = provider.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<SalesCoreUser>>();
        var user = await users.FindByNameAsync(target.UserName) ?? throw new InvalidOperationException($"利用者 {target.UserName} が無い。");
        var result = await change(users, user);
        Assert.True(result.Succeeded, string.Join(" ", result.Errors.Select(e => e.Description)));
    }
}
