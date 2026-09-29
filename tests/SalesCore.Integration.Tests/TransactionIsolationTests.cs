using Microsoft.EntityFrameworkCore;

namespace SalesCore.Integration.Tests;

/// <summary>
/// 結合テストが互いに独立していることを確かめる。テストが書いた行は、テストが終われば消えていなければならない。
/// 検証用の表はトランザクションの外で作るので、戻し損ねた行はここに残り、次の実行でこのテストが落ちる。
/// つまり「テストを2回続けて実行して2回とも通る」ことが、状態が積み上がらないことの証拠になる。
/// </summary>
public class TransactionIsolationTests(DatabaseFixture fixture) : DatabaseTest(fixture)
{
    private const string ProbeTable = "test_isolation_probe";

    [Fact]
    public async Task 前の実行で書いた行は残っておらず_書いた行はテストの中でだけ見える()
    {
        await using (var setup = Fixture.CreateContext())
        {
            await setup.Database.ExecuteSqlRawAsync(
                $"CREATE TABLE IF NOT EXISTS {ProbeTable} (id serial PRIMARY KEY, written_at timestamptz NOT NULL DEFAULT now())");
        }

        Assert.Equal(0, await CountRowsAsync());

        await Db.Database.ExecuteSqlRawAsync($"INSERT INTO {ProbeTable} DEFAULT VALUES");

        Assert.Equal(1, await CountRowsAsync());
    }

    private Task<int> CountRowsAsync() =>
        Db.Database.SqlQueryRaw<int>($"SELECT count(*)::int AS \"Value\" FROM {ProbeTable}").SingleAsync();
}
