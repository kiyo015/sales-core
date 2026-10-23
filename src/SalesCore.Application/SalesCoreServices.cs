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
            .AddEntityFrameworkStores<SalesCoreIdentityDbContext>();
        services.ConfigureApplicationCookie(options => options.LoginPath = loginPath);
        services.AddScoped<AccountUseCases>();
        return services;
    }
}
