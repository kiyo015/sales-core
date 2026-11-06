using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SalesCore.Application;
using SalesCore.Domain;

namespace SalesCore.E2E.Tests;

/// <summary>画面にログインする利用者。パスワードは実行ごとに作り、どこにも書き出さない。</summary>
public sealed record E2EUser(string UserName, string Password);

/// <summary>
/// 端から端までのテストで使うマスタと利用者。段階1の API のテスト(InvoiceApiTests)と同じ形:
/// 本社(月末締め)・支店(請求先は本社)、ボルト(105円・10%)とお茶(33.33円・8%)、倉庫1件。
/// <para>
/// 端から端まで用のDBは画面のプロセスが書いたデータが残るので、コードと利用者名は実行ごとに変える
/// (前の実行のデータと混ざらず、消さなくてよい。業務のデータは物理削除しない)。
/// </para>
/// </summary>
public sealed record E2EData(string HeadOffice, string Branch, string Bolt, string Tea, string Warehouse, E2EUser Sales, E2EUser Accounting)
{
    public static async Task<E2EData> SeedAsync(WebAppFixture fixture)
    {
        var run = DateTime.UtcNow.ToString("MMddHHmmssfff"); // 13文字。コードは20文字まで
        var data = new E2EData($"H{run}", $"B{run}", $"P{run}A", $"P{run}B", $"W{run}",
            new E2EUser($"sales-{run}", NewPassword()), new E2EUser($"accounting-{run}", NewPassword()));

        await using (var db = fixture.CreateContext())
        {
            // 税区分と税率(標準10%・軽減8%)はマイグレーションで入っている(本人の決定 2026-11-02)
            var standard = await db.TaxCategories.SingleAsync(t => t.Code == "STD");
            var reduced = await db.TaxCategories.SingleAsync(t => t.Code == "RED");
            var headOffice = new Customer(data.HeadOffice, "E2E 本社", 31);
            db.AddRange(
                new Customer(data.Branch, "E2E 大阪支店", 31, billingCustomer: headOffice),
                new Product(data.Bolt, "E2E ボルト", "箱", standard, 105m),
                new Product(data.Tea, "E2E お茶", "本", reduced, 33.33m),
                new Warehouse(data.Warehouse, "E2E 倉庫"));
            await db.SaveChangesAsync();
        }

        // 利用者は画面と同じ登録(AddSalesCoreIdentity)で作る。役割は画面が起動の時に用意している
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSalesCore(fixture.ConnectionString);
        services.AddSalesCoreIdentity("/account/login");
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var account = scope.ServiceProvider.GetRequiredService<AccountUseCases>();
        await account.EnsureRolesAsync();
        await account.EnsureUserAsync(data.Sales.UserName, "E2E 営業", data.Sales.Password, [Roles.Sales]);
        await account.EnsureUserAsync(data.Accounting.UserName, "E2E 経理", data.Accounting.Password, [Roles.Accounting]);
        return data;
    }

    // 12文字以上(業務ルール集「8. 利用者とログイン」)。乱数で作る
    private static string NewPassword() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
}
