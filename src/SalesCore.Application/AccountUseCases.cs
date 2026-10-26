using Microsoft.AspNetCore.Identity;
using SalesCore.Domain;
using SalesCore.Infrastructure.Persistence.Identity;

namespace SalesCore.Application;

/// <summary>ログインの結果。失敗の理由は、ロック中かどうかだけを伝える(利用者名を探られないため)。</summary>
public enum SignInOutcome
{
    Succeeded,
    Failed,
    LockedOut,
}

/// <summary>
/// ログイン・ログアウトと利用者の作成(業務ルール集「8. 利用者とログイン」)。
/// 画面は Identity(Infrastructure の利用者の型)を直接触らず、ここを呼ぶ。
/// パスワードの規則とロックの回数・時間は <see cref="SalesCoreServices.AddSalesCoreIdentity"/> で設定している。
/// </summary>
public sealed class AccountUseCases(SignInManager<SalesCoreUser> signIn, UserManager<SalesCoreUser> users, RoleManager<IdentityRole> roles)
{
    // 利用者が無い・無効な時に、パスワード違いと同じ計算量にするためのダミーのハッシュ(パスワードとしては使われない)
    private static readonly SalesCoreUser DummyUser = new();
    private static readonly Lazy<string> DummyHash = new(() => new PasswordHasher<SalesCoreUser>().HashPassword(DummyUser, Guid.NewGuid().ToString()));

    public async Task<SignInOutcome> SignInAsync(string? userName, string? password)
    {
        // 空のまま送られたフォームでは null が来る。Identity は null で例外を投げるので、ここで失敗にする
        if (string.IsNullOrEmpty(userName) || string.IsNullOrEmpty(password)) return SignInOutcome.Failed;

        var user = await users.FindByNameAsync(userName);
        // 無効な利用者は、正しいパスワードでも入れない。利用者が無い時と同じ失敗にする(区別すると利用者名を探られる)
        if (user is null || !user.IsActive)
        {
            // 応答時間でも区別させない。ハッシュの計算(PBKDF2)を同じだけ行ってから返す。
            // 2026-10-23 実測: そろえる前は、利用者が無い時 3.1ms、パスワード違い 192.4ms で、時間だけで有無が分かった
            users.PasswordHasher.VerifyHashedPassword(DummyUser, DummyHash.Value, password);
            return SignInOutcome.Failed;
        }

        // lockoutOnFailure: true で、失敗の回数を数え、5回でロックする。成功すると回数は0に戻る
        var result = await signIn.PasswordSignInAsync(user, password, isPersistent: false, lockoutOnFailure: true);
        return result.Succeeded ? SignInOutcome.Succeeded
            : result.IsLockedOut ? SignInOutcome.LockedOut
            : SignInOutcome.Failed;
    }

    public Task SignOutAsync() => signIn.SignOutAsync();

    /// <summary>役割(営業・経理・管理者)を用意する。無いものだけ作る(起動のたびに呼んでよい)。</summary>
    public async Task EnsureRolesAsync()
    {
        foreach (var name in Roles.All)
        {
            if (await roles.RoleExistsAsync(name)) continue;
            var result = await roles.CreateAsync(new IdentityRole(name));
            // 起動の時に呼ぶので、失敗したら起動を止める(役割の無いまま動かさない)
            if (!result.Succeeded) throw new InvalidOperationException($"役割「{name}」を作れなかった: {Describe(result)}");
        }
    }

    /// <summary>
    /// 開発用の初期の利用者(パスワードは各自の user-secrets)を作り、役割を付ける。既にいれば何もしない(パスワードも役割も変えない)。
    /// 作ったら true。<see cref="Roles"/> に無い役割は <see cref="InvalidInputException"/>。
    /// </summary>
    public async Task<bool> EnsureUserAsync(string userName, string displayName, string password, IReadOnlyCollection<string>? roleNames = null)
    {
        // 設定の誤りには、利用者が既にいても気付けるよう、先に確かめる
        var wanted = (roleNames ?? []).Distinct().ToList();
        var unknown = wanted.Except(Roles.All).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidInputException($"決めていない役割: {string.Join("、", unknown)}。役割は {string.Join("・", Roles.All)} だけ。");
        }
        if (await users.FindByNameAsync(userName) is not null) return false;

        // 利用者を作ってから役割の付与に失敗すると、役割の無い利用者が残り、次の起動では「既にいる」ので二度と付かない。
        // 付与で失敗しうる原因(役割がまだ無い)を、作る前に確かめる
        foreach (var name in wanted)
        {
            if (!await roles.RoleExistsAsync(name))
            {
                throw new InvalidOperationException($"役割「{name}」が用意されていない。EnsureRolesAsync を先に呼ぶ。");
            }
        }

        await CreateUserAsync(userName, displayName, password);
        if (wanted.Count > 0)
        {
            var result = await users.AddToRolesAsync((await users.FindByNameAsync(userName))!, wanted);
            if (!result.Succeeded) throw new InvalidOperationException($"利用者「{userName}」に役割を付けられなかった: {Describe(result)}");
        }
        return true;
    }

    private static string Describe(IdentityResult result) => string.Join(" ", result.Errors.Select(e => e.Description));

    /// <summary>利用者を作る。パスワードが規則に合わない・利用者名が重複しているなどは <see cref="InvalidInputException"/>。</summary>
    public async Task CreateUserAsync(string userName, string displayName, string password)
    {
        var result = await users.CreateAsync(new SalesCoreUser { UserName = userName, DisplayName = displayName }, password);
        if (!result.Succeeded)
        {
            throw new InvalidInputException(string.Join(" ", result.Errors.Select(e => e.Description)));
        }
    }
}
