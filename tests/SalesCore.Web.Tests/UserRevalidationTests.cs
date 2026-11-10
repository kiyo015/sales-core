using Microsoft.Extensions.Configuration;
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

    [Fact]
    public void 設定があれば_その間隔で確かめ直す()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SalesCore:UserRevalidationInterval"] = "00:00:02" })
            .Build();

        Assert.Equal(TimeSpan.FromSeconds(2), UserRevalidation.IntervalFrom(configuration));
    }

    // 0 だと、確かめ直しが待たずに回り続け、DB に問い合わせ続ける。設定の誤りは起動で止める
    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:01:00")]
    public void 間隔が0以下なら_設定の誤りとして止める(string interval)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SalesCore:UserRevalidationInterval"] = interval })
            .Build();

        var error = Assert.Throws<InvalidOperationException>(() => UserRevalidation.IntervalFrom(configuration));
        Assert.Contains("SalesCore:UserRevalidationInterval", error.Message);
    }
}
