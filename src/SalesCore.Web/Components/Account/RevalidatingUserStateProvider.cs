using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using SalesCore.Application;

namespace SalesCore.Web.Components.Account;

/// <summary>
/// 開いたままの画面(Blazor の回線)の利用者を、Cookie と同じ間隔(設定の半分)で確かめ直す。決まりは Application の
/// <see cref="UserRevalidation.IsStillSignedInAsync"/>(無効・変更の印・役割)。通らなければログインしていない扱いになり、
/// <see cref="Shared.OperationPage"/> がログインの画面へ送る。回線だけの部品なので Web に置く(PR #20 の AIレビュー)。
/// </summary>
public sealed class RevalidatingUserStateProvider(
    ILoggerFactory loggers, IServiceScopeFactory scopes, IOptions<SecurityStampValidatorOptions> options)
    : RevalidatingServerAuthenticationStateProvider(loggers)
{
    protected override TimeSpan RevalidationInterval => options.Value.ValidationInterval;

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken cancellationToken)
    {
        // 回線は長く生きるので、確かめるたびに新しい範囲で DB を読む
        await using var scope = scopes.CreateAsyncScope();
        return await UserRevalidation.IsStillSignedInAsync(scope.ServiceProvider, state.User);
    }
}
