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

    public async Task DisposeAsync()
    {
        if (_transaction is not null)
        {
            await _transaction.DisposeAsync(); // コミットしていないので、ここで取り消される
        }
        await Db.DisposeAsync();
    }
}
