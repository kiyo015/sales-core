using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using SalesCore.Infrastructure.Persistence.Identity;

namespace SalesCore.Application;

/// <summary>
/// ログインした利用者の情報(クレーム)。Identity の標準(Id・利用者名・役割)に、画面に出す表示名を足す。
/// 画面は Infrastructure の利用者の型を読まず、このクレームから表示名を出す。
/// </summary>
public sealed class SalesCoreClaimsFactory(
    UserManager<SalesCoreUser> users, RoleManager<IdentityRole> roles, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<SalesCoreUser, IdentityRole>(users, roles, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(SalesCoreUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(SalesCoreClaims.DisplayName, user.DisplayName));
        return identity;
    }
}
