using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SalesCore.Application;
using SalesCore.Domain;
using SalesCore.Infrastructure.Persistence.Identity;

namespace SalesCore.Integration.Tests;

/// <summary>
/// ログインの決まり(業務ルール集「8. 利用者とログイン」)。サーバーを立てず、画面と同じ登録(AddSalesCoreIdentity)と
/// HTTP のコンテキストを用意して、ログインで認証の Cookie が出ること・ログアウトで消えることまで確かめる。
/// 利用者は Identity の DbContext に、このテストのトランザクションの中で作る(終わりに取り消される)。
/// </summary>
public class AccountTests(DatabaseFixture fixture) : DatabaseTest(fixture)
{
    private const string Password = "correct horse battery"; // テスト用の値(12文字以上、記号・数字なし)
    private const string AuthCookie = ".AspNetCore.Identity.Application";


    [Fact]
    public async Task 正しいパスワードでログインでき_認証のCookieが出る()
    {
        var (account, http, _) = await SetUpAsync();
        await account.CreateUserAsync("yamada", "山田 太郎", Password);

        Assert.Equal(SignInOutcome.Succeeded, await account.SignInAsync("yamada", Password));
        Assert.Contains(SetCookies(http), c => c.StartsWith(AuthCookie + "=", StringComparison.Ordinal) && !IsExpired(c));
    }

    [Fact]
    public async Task ログアウトすると_認証のCookieが消される()
    {
        var (account, http, _) = await SetUpAsync();
        await account.CreateUserAsync("yamada", "山田 太郎", Password);
        await account.SignInAsync("yamada", Password);
        http.Response.Headers.Remove("Set-Cookie");

        await account.SignOutAsync();

        Assert.Contains(SetCookies(http), c => c.StartsWith(AuthCookie + "=", StringComparison.Ordinal) && IsExpired(c));
    }

    [Fact]
    public async Task パスワードが違う時と利用者名が無い時は_同じ失敗になる()
    {
        var (account, http, _) = await SetUpAsync();
        await account.CreateUserAsync("yamada", "山田 太郎", Password);

        Assert.Equal(SignInOutcome.Failed, await account.SignInAsync("yamada", "wrong password!!"));
        Assert.Equal(SignInOutcome.Failed, await account.SignInAsync("nobody", Password));
        Assert.DoesNotContain(SetCookies(http), c => c.StartsWith(AuthCookie + "=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 続けて4回間違えても_正しいパスワードなら入れる()
    {
        var (account, _, _) = await SetUpAsync();
        await account.CreateUserAsync("yamada", "山田 太郎", Password);

        for (var i = 0; i < 4; i++) await account.SignInAsync("yamada", "wrong password!!");

        Assert.Equal(SignInOutcome.Succeeded, await account.SignInAsync("yamada", Password));
    }

    // 「4回でも入れる」だけでは、成功しても回数を戻さない実装でも通る(PR #9 の AIレビューの指摘)。
    // 4回 → 成功 → また4回、でロックされないことで、成功で回数が0に戻ることを確かめる
    [Fact]
    public async Task ログインに成功すると_失敗の回数は0に戻る()
    {
        var (account, _, _) = await SetUpAsync();
        await account.CreateUserAsync("yamada", "山田 太郎", Password);

        for (var i = 0; i < 4; i++) await account.SignInAsync("yamada", "wrong password!!");
        Assert.Equal(SignInOutcome.Succeeded, await account.SignInAsync("yamada", Password));
        for (var i = 0; i < 4; i++) await account.SignInAsync("yamada", "wrong password!!");

        Assert.Equal(SignInOutcome.Succeeded, await account.SignInAsync("yamada", Password));
    }

    [Theory]
    [InlineData("", Password)]
    [InlineData(null, Password)]
    [InlineData("yamada", "")]
    [InlineData("yamada", null)]
    public async Task 利用者名かパスワードが空なら_例外でなく失敗になる(string? userName, string? password)
    {
        var (account, _, _) = await SetUpAsync();
        await account.CreateUserAsync("yamada", "山田 太郎", Password);

        Assert.Equal(SignInOutcome.Failed, await account.SignInAsync(userName, password));
    }

    [Fact]
    public async Task 続けて5回間違えるとロックされ_正しいパスワードでも入れない()
    {
        var (account, _, _) = await SetUpAsync();
        await account.CreateUserAsync("yamada", "山田 太郎", Password);

        for (var i = 0; i < 5; i++) await account.SignInAsync("yamada", "wrong password!!");

        Assert.Equal(SignInOutcome.LockedOut, await account.SignInAsync("yamada", Password));
    }

    // .NET 8 の Identity(8.0.31)は、ロックの期限の計算と判定に差し替えた時計(TimeProvider)を使わず、実際の時計(UtcNow)を使う
    // (2026-10-23 に、時計を15分進めても解けないことで気付き、DLL に TimeProvider が無いことを確かめた)。
    // そこで、期限がロックした時刻のちょうど15分後に入ることと、期限を過ぎれば入れることを分けて確かめる
    [Fact]
    public async Task ロックの期限は15分後で_期限を過ぎれば入れる()
    {
        var (account, _, identity) = await SetUpAsync();
        await account.CreateUserAsync("yamada", "山田 太郎", Password);

        var before = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++) await account.SignInAsync("yamada", "wrong password!!");
        var after = DateTimeOffset.UtcNow;

        var user = await identity.Users.SingleAsync(u => u.UserName == "yamada");
        Assert.InRange(user.LockoutEnd!.Value, before + TimeSpan.FromMinutes(15), after + TimeSpan.FromMinutes(15));

        user.LockoutEnd = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30); // 期限の前
        await identity.SaveChangesAsync();
        Assert.Equal(SignInOutcome.LockedOut, await account.SignInAsync("yamada", Password));

        user.LockoutEnd = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1); // 期限を過ぎた
        await identity.SaveChangesAsync();
        Assert.Equal(SignInOutcome.Succeeded, await account.SignInAsync("yamada", Password));
    }

    [Fact]
    public async Task 無効にした利用者は_正しいパスワードでも入れない()
    {
        var (account, http, identity) = await SetUpAsync();
        await account.CreateUserAsync("yamada", "山田 太郎", Password);
        var user = await identity.Users.SingleAsync(u => u.UserName == "yamada");
        user.IsActive = false;
        await identity.SaveChangesAsync();

        Assert.Equal(SignInOutcome.Failed, await account.SignInAsync("yamada", Password));
        Assert.DoesNotContain(SetCookies(http), c => c.StartsWith(AuthCookie + "=", StringComparison.Ordinal));
    }

    // is_active の DB の既定値は true。EF Core が bool の false を「設定していない」とみなして INSERT から省くと、
    // 無効のつもりで作った利用者が有効で入る(PR #9 の AIレビューの指摘)。2026-10-23 に EF Core 8.0.31 で試すと
    // 再現せず、false で保存された。EF の版を上げた時に振る舞いが変わっても気付けるよう、テストとして残す
    [Fact]
    public async Task 無効のまま作った利用者は_無効のまま保存される()
    {
        var (_, _, identity) = await SetUpAsync();
        identity.Users.Add(new SalesCoreUser { UserName = "suzuki", NormalizedUserName = "SUZUKI", DisplayName = "鈴木", IsActive = false });
        await identity.SaveChangesAsync();

        var saved = await Db.Database
            .SqlQueryRaw<bool>("SELECT is_active AS \"Value\" FROM \"AspNetUsers\" WHERE \"UserName\" = 'suzuki'")
            .SingleAsync();
        Assert.False(saved);
    }

    [Theory]
    [InlineData("abcdefghijk")] // 11文字
    [InlineData("")]
    public async Task パスワードが12文字未満なら_利用者を作れない(string password)
    {
        var (account, _, _) = await SetUpAsync();

        await Assert.ThrowsAsync<InvalidInputException>(() => account.CreateUserAsync("yamada", "山田 太郎", password));
    }

    [Fact]
    public async Task パスワードは12文字あれば_文字の種類を問わない()
    {
        var (account, _, _) = await SetUpAsync();

        await account.CreateUserAsync("yamada", "山田 太郎", "abcdefghijkl"); // 英小文字だけの12文字

        Assert.Equal(SignInOutcome.Succeeded, await account.SignInAsync("yamada", "abcdefghijkl"));
    }

    // --- 役割(業務ルール集「8.」の「役割」、2026-10-26) ---

    [Fact]
    public async Task 役割を用意すると_営業と経理と管理者の3つができ_何度呼んでも増えない()
    {
        var (account, _, identity) = await SetUpAsync();

        await account.EnsureRolesAsync();
        await account.EnsureRolesAsync();

        Assert.Equal(new[] { "営業", "管理者", "経理" }, await identity.Roles.Select(r => r.Name!).OrderBy(n => n).ToListAsync());
    }

    [Fact]
    public async Task 初期の利用者に役割を複数付けると_ログインした利用者はその役割とIdと表示名を持つ()
    {
        var (account, http, identity) = await SetUpAsync();
        await account.EnsureRolesAsync();

        await account.EnsureUserAsync("tanaka", "田中 一郎", Password, [Roles.Sales, Roles.Accounting]);

        var user = await identity.Users.SingleAsync(u => u.UserName == "tanaka");
        var principal = await http.RequestServices.GetRequiredService<SignInManager<SalesCoreUser>>().CreateUserPrincipalAsync(user);
        Assert.True(principal.IsInRole(Roles.Sales));
        Assert.True(principal.IsInRole(Roles.Accounting));
        Assert.False(principal.IsInRole(Roles.Admin));
        Assert.Equal(user.Id, principal.FindFirstValue(ClaimTypes.NameIdentifier)); // 作成者・監査ログに入る Id
        Assert.Equal("田中 一郎", principal.FindFirstValue(SalesCoreClaims.DisplayName)); // 画面の上部に出す名前
    }

    [Fact]
    public async Task 決めていない役割は_付けられない()
    {
        var (account, _, _) = await SetUpAsync();
        await account.EnsureRolesAsync();

        await Assert.ThrowsAsync<InvalidInputException>(() => account.EnsureUserAsync("sato", "佐藤", Password, ["社長"]));
    }

    [Fact]
    public async Task 初期の利用者は_既にいれば作り直さずパスワードも変えない()
    {
        var (account, _, _) = await SetUpAsync();

        Assert.True(await account.EnsureUserAsync("admin", "管理者", Password));
        Assert.False(await account.EnsureUserAsync("admin", "管理者", "another password!!"));

        Assert.Equal(SignInOutcome.Succeeded, await account.SignInAsync("admin", Password));
    }

    [Fact]
    public async Task DBにはパスワードの平文が無く_ハッシュだけが入る()
    {
        var (account, _, _) = await SetUpAsync();
        await account.CreateUserAsync("yamada", "山田 太郎", Password);

        // 利用者の行の全部の列を1つの文字列にして、平文が入っていないことを確かめる
        var row = await Db.Database
            .SqlQueryRaw<string>("SELECT row_to_json(u)::text AS \"Value\" FROM \"AspNetUsers\" u WHERE \"UserName\" = 'yamada'")
            .SingleAsync();
        Assert.DoesNotContain(Password, row);
        var hash = await Db.Database
            .SqlQueryRaw<string>("SELECT \"PasswordHash\" AS \"Value\" FROM \"AspNetUsers\" WHERE \"UserName\" = 'yamada'")
            .SingleAsync();
        Assert.StartsWith("AQAAAA", hash); // Identity の v3 形式(PBKDF2)。先頭の1バイトが形式の番号
    }

    private SalesCoreIdentityDbContext IdentityContext()
    {
        var context = new SalesCoreIdentityDbContext(
            new DbContextOptionsBuilder<SalesCoreIdentityDbContext>().UseNpgsql(Db.Database.GetDbConnection()).Options);
        JoinTransaction(context);
        return context;
    }

    /// <summary>画面と同じ登録(AddSalesCoreIdentity)で、ログインの入口と HTTP のコンテキストを作る。</summary>
    private Task<(AccountUseCases Account, HttpContext Http, SalesCoreIdentityDbContext Identity)> SetUpAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => IdentityContext());
        services.AddSalesCoreIdentity("/account/login");
        var provider = services.BuildServiceProvider().CreateScope().ServiceProvider;

        var http = new DefaultHttpContext { RequestServices = provider };
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;
        return Task.FromResult((provider.GetRequiredService<AccountUseCases>(), (HttpContext)http, provider.GetRequiredService<SalesCoreIdentityDbContext>()));
    }

    private static string[] SetCookies(HttpContext http) => http.Response.Headers.SetCookie.Select(c => c ?? "").ToArray();

    private static bool IsExpired(string setCookie) => setCookie.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase);
}
