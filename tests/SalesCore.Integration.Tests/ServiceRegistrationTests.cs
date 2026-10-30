using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SalesCore.Application;

namespace SalesCore.Integration.Tests;

/// <summary>
/// 画面と同じ登録(AddSalesCore・AddSalesCoreIdentity)で、すべてのユースケースを作れる。DB には繋がない。
/// 画面のテスト(MasterPageTests)はユースケースを手で組み立てるので、登録を忘れても気付けない(2026-10-30 に書き出した、どのテストも通らない経路)。
/// </summary>
public class ServiceRegistrationTests
{
    [Fact]
    public void 画面と同じ登録で_すべてのユースケースを作れる()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddSalesCore("Host=localhost;Database=not_used_test");
        services.AddSalesCoreIdentity(loginPath: "/account/login");
        using var scope = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }).CreateScope();

        var useCases = typeof(SalesOrderUseCases).Assembly.GetExportedTypes()
            .Where(type => type.Name.EndsWith("UseCases", StringComparison.Ordinal))
            .ToList();

        Assert.Contains(typeof(MasterUseCases), useCases); // 調べ方の確認
        Assert.All(useCases, type => Assert.NotNull(scope.ServiceProvider.GetService(type)));
    }
}
