using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesCore.Infrastructure.Persistence.Identity;

namespace SalesCore.Application;

/// <summary>
/// ログインしている人を、決まった間隔で DB の利用者と突き合わせ直す(画面だけ。業務ルール集「8. 利用者とログイン」)。
/// パスワードを変えると Identity の変更の印(セキュリティスタンプ)が変わり、無効にすると <see cref="SalesCoreUser.IsActive"/> が false になる。
/// 役割の変更は印を変えないので、開いたままの画面では役割そのものを突き合わせる(<see cref="IsStillSignedInAsync"/>。回線の部品は Web にある)。
/// どれも、間隔のうちにログインの画面へ戻す。間隔は設定 SalesCore:UserRevalidationInterval(既定1分。本人の決定 2026-11-10)。
/// <para>
/// 確かめ直す前は、役割を外しても無効にしても、Cookie の有効期限(最長14日)まで、開いたままの画面ではいつまでも使えた(2026-11-10 実測)。
/// </para>
/// </summary>
public static class UserRevalidation
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 設定の間隔。1秒以上1分以内にだけ変えられる(テストで短くする)。1分を超えると「1分以内に効く」が守れず、
    /// 0以下やごく短い値は確かめ直しが待たずに回り続けて DB に問い合わせ続ける(半分にすると 0 にもなる。PR #20 の AIレビュー)。
    /// 空の値も既定に黙って戻さない(承認の基準額と同じ。2026-10-30)。
    /// どれも設定の誤りとして止める(画面は起動の時に呼ぶ)。
    /// </summary>
    public static TimeSpan IntervalFrom(IConfiguration configuration)
    {
        const string key = "SalesCore:UserRevalidationInterval";
        if (configuration[key] is not { } value) return DefaultInterval;
        return TimeSpan.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var interval) && interval >= MinimumInterval && interval <= DefaultInterval
            ? interval
            : throw new InvalidOperationException($"設定 {key}({value})は、1秒以上1分以内の時間(例: 00:00:30)にする。");
    }

    /// <summary>Cookie の確かめ直しの間隔。無効の検査は <see cref="SalesCoreSignInManager"/>(AddSalesCoreIdentity で登録)。</summary>
    internal static void AddUserRevalidation(this IServiceCollection services)
    {
        // Cookie(画面を開くたびの要求): 間隔を過ぎた Cookie を、Identity の SecurityStampValidator が確かめ直す。
        // 間隔は設定の半分。変更の直後に開き直すと、まだ確かめ直していない Cookie で回線が始まり、回線の最初の確かめ直しまで古いまま残る。
        // Cookie と回線の両方を半分ごとにすれば、合わせて設定の時間以内に効く(既定で最長1分。PR #20 の AIレビュー)
        services.AddOptions<SecurityStampValidatorOptions>()
            .Configure<IConfiguration>((options, configuration) => options.ValidationInterval = IntervalFrom(configuration) / 2);
    }

    /// <summary>
    /// 開いたままの画面の利用者を、Cookie と同じ決まり(<see cref="SalesCoreSignInManager"/>)で確かめ直し、役割も DB と突き合わせる。
    /// Identity は役割を足し外ししても変更の印を変えない(2026-11-10 実測)。Cookie は確かめ直すたびに DB から作り直すので新しい役割になるが、
    /// 回線は作り直さないので、役割が違えば通さない(ログインの画面へ戻し、Cookie の新しい役割で使い直してもらう)。
    /// 回線の部品(Web の RevalidatingUserStateProvider)が、Cookie と同じ間隔(設定の半分)で、確かめるたびに新しい範囲で呼ぶ。
    /// </summary>
    public static async Task<bool> IsStillSignedInAsync(IServiceProvider services, ClaimsPrincipal user)
    {
        var signIn = services.GetRequiredService<SignInManager<SalesCoreUser>>();
        if (await signIn.ValidateSecurityStampAsync(user) is not { } found) return false;
        var roleClaim = (user.Identity as ClaimsIdentity)?.RoleClaimType ?? ClaimTypes.Role;
        var signedInRoles = user.FindAll(roleClaim).Select(c => c.Value);
        return (await signIn.UserManager.GetRolesAsync(found)).ToHashSet().SetEquals(signedInRoles);
    }
}

/// <summary>確かめ直す時に、変更の印に加えて、無効にした利用者も通さない(Identity の既定は印しか見ない)。</summary>
public sealed class SalesCoreSignInManager(
    UserManager<SalesCoreUser> users,
    IHttpContextAccessor http,
    IUserClaimsPrincipalFactory<SalesCoreUser> claims,
    IOptions<IdentityOptions> options,
    ILogger<SignInManager<SalesCoreUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<SalesCoreUser> confirmation)
    : SignInManager<SalesCoreUser>(users, http, claims, options, logger, schemes, confirmation)
{
    public override async Task<bool> ValidateSecurityStampAsync(SalesCoreUser? user, string? securityStamp) =>
        user is { IsActive: true } && await base.ValidateSecurityStampAsync(user, securityStamp);
}
