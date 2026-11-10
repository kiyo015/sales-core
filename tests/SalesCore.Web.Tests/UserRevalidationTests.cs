using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SalesCore.Application;

namespace SalesCore.Web.Tests;

/// <summary>ログインしている人を確かめ直す間隔(本人の決定 2026-11-10: 1分以内)。</summary>
public class UserRevalidationTests
{
    [Fact]
    public void 設定が無ければ_1分ごとに確かめ直す()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), UserRevalidation.IntervalFrom(new ConfigurationBuilder().Build()));
    }

    [Theory]
    [InlineData("00:00:02", 2)]
    [InlineData("00:01:00", 60)]
    public void 設定があれば_その間隔で確かめ直す(string interval, int seconds)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SalesCore:UserRevalidationInterval"] = interval })
            .Build();

        Assert.Equal(TimeSpan.FromSeconds(seconds), UserRevalidation.IntervalFrom(configuration));
    }

    // 変更の直後に開き直すと、まだ確かめ直していない Cookie で回線が始まり、回線の最初の確かめ直しまで古いまま残る。
    // Cookie と回線の両方を設定の半分ごとに確かめれば、合わせて設定の時間以内に効く(PR #20 の AIレビュー)
    [Theory]
    [InlineData(null, 30)]
    [InlineData("00:00:02", 1)]
    public void Cookieも開いたままの画面も_設定の半分ごとに確かめ直す(string? interval, int seconds)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SalesCore:UserRevalidationInterval"] = interval })
            .Build());
        services.AddSalesCoreIdentity("/account/login");
        using var provider = services.BuildServiceProvider();

        Assert.Equal(TimeSpan.FromSeconds(seconds), provider.GetRequiredService<IOptions<SecurityStampValidatorOptions>>().Value.ValidationInterval);
    }

    // 0 だと、確かめ直しが待たずに回り続け、DB に問い合わせ続ける。設定の誤りは起動で止める
    // 1分を超えると「1分以内に効く」(業務ルール集「8.」)が守れない。空の値も既定に黙って戻さない(承認の基準額と同じ。2026-10-30)
    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:01:00")]
    [InlineData("00:01:01")]
    [InlineData("")]
    public void 間隔が0以下か1分を超えるか空なら_設定の誤りとして止める(string interval)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SalesCore:UserRevalidationInterval"] = interval })
            .Build();

        var error = Assert.Throws<InvalidOperationException>(() => UserRevalidation.IntervalFrom(configuration));
        Assert.Contains("SalesCore:UserRevalidationInterval", error.Message);
    }
}
