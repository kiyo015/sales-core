using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SalesCore.Infrastructure.Persistence;

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
        services.AddScoped<SalesOrderUseCases>();
        services.AddScoped<InvoiceUseCases>();
        services.AddSingleton(TimeProvider.System); // 「今日」の出どころ。テストでは固定の時刻に差し替える
        return services;
    }
}
