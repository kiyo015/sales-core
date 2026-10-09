using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SalesCore.Infrastructure.Persistence;

namespace SalesCore.Integration.Tests;

/// <summary>
/// DBを使うテストの基底クラス。テストごとにトランザクションを始め、終わったらコミットせずに捨てる
/// (＝ロールバック)。テストが書いたデータは次のテストにも次の実行にも残らない。
/// テストの中で <see cref="Db"/> を使う限り、この性質が保たれる。
/// </summary>
[Collection(DatabaseCollection.Name)]
public abstract class DatabaseTest(DatabaseFixture fixture) : IAsyncLifetime
{
    private IDbContextTransaction? _transaction;

    protected DatabaseFixture Fixture { get; } = fixture;

    protected SalesCoreDbContext Db { get; } = fixture.CreateContext();

    public async Task InitializeAsync() => _transaction = await Db.Database.BeginTransactionAsync();

    /// <summary>
    /// もう1人の利用者(別の画面・別の API 呼び出し)にあたる DbContext。同じ接続とトランザクションを使うので、
    /// 書いたデータはテストの終わりに一緒に取り消される。呼び出した側で破棄する。
    /// </summary>
    protected SalesCoreDbContext CreateAnotherSession()
    {
        var other = new SalesCoreDbContext(
            new DbContextOptionsBuilder<SalesCoreDbContext>()
                .UseNpgsql(Db.Database.GetDbConnection())
                .Options);
        other.Database.UseTransaction(_transaction!.GetDbTransaction());
        return other;
    }

    public async Task DisposeAsync()
    {
        if (_transaction is not null)
        {
            await _transaction.DisposeAsync(); // コミットしていないので、ここで取り消される
        }
        await Db.DisposeAsync();
    }
}
