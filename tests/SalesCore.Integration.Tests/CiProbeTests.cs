using Microsoft.EntityFrameworkCore;

namespace SalesCore.Integration.Tests;

/// <summary>対照実験(Day28): CI でテストが落ちると PR をマージできないことの確認。確認したら消す。</summary>
public class CiProbeTests(DatabaseFixture fixture) : DatabaseTest(fixture)
{
    [Fact]
    public async Task わざと落ちる()
    {
        var one = await Db.Database.SqlQueryRaw<int>("SELECT 1 AS \"Value\"").SingleAsync();

        Assert.Equal(2, one);
    }
}
