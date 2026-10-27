using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SalesCore.Infrastructure.Persistence;
using SalesCore.Infrastructure.Persistence.Identity;

namespace SalesCore.Application;

/// <summary>
/// ユースケースと、それが使う DB・時計の登録。API と画面(Web)の両方がこれだけを呼ぶ。
/// 画面が DbContext(Infrastructure)を直接触らずに済むよう、組み立てを Application に置く。
/// </summary>
public static class SalesCoreServices
{
    public static IServiceCollection AddSalesCore(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<SalesCoreDbContext>(options => options.UseNpgsql(connectionString));
        // 利用者と役割は別の DbContext(同じ DB、履歴の表は別)。dotnet ef が API から見つけられるよう、ここで登録する
        services.AddDbContext<SalesCoreIdentityDbContext>(options => options.UseNpgsql(
            connectionString, npgsql => npgsql.MigrationsHistoryTable(SalesCoreIdentityDbContext.MigrationsHistoryTable)));
        services.AddScoped<SalesOrderUseCases>();
        services.AddScoped<InvoiceUseCases>();
        services.AddSingleton(TimeProvider.System); // 「今日」の出どころ。テストでは固定の時刻に差し替える
        // 操作した人(作成者・更新者・監査ログ)。画面は Blazor の認証の状態、API は HTTP の利用者から読む(LoggedInUser)
        services.AddHttpContextAccessor();
        // 同じ利用者を、権限の検査(ICurrentUser)と操作した人(ICurrentUserSource)の両方に使う
        services.AddScoped(provider => new LoggedInUser(
            provider.GetRequiredService<IHttpContextAccessor>(), provider.GetService<AuthenticationStateProvider>()));
        services.AddScoped<ICurrentUser>(provider => provider.GetRequiredService<LoggedInUser>());
        services.AddScoped<ICurrentUserSource>(provider => provider.GetRequiredService<ICurrentUser>());
        return services;
    }

    /// <summary>
    /// ログイン(ASP.NET Core Identity と Cookie)。画面だけが呼ぶ。
    /// 規則は業務ルール集「8. 利用者とログイン」: パスワードは12文字以上で種類を問わない、5回続けて間違えると15分ロック。
    /// </summary>
    public static IServiceCollection AddSalesCoreIdentity(this IServiceCollection services, string loginPath)
    {
        services
            .AddIdentity<SalesCoreUser, IdentityRole>(options =>
            {
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredUniqueChars = 1;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.Lockout.AllowedForNewUsers = true;
            })
            .AddEntityFrameworkStores<SalesCoreIdentityDbContext>()
            .AddClaimsPrincipalFactory<SalesCoreClaimsFactory>(); // 表示名をクレームに足す
        services.ConfigureApplicationCookie(options => options.LoginPath = loginPath);
        services.AddScoped<AccountUseCases>();
        return services;
    }
}
