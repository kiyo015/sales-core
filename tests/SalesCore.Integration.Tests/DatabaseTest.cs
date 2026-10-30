using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    /// interceptors を渡すと、保存の前後に割り込める(同時に保存した場面の再現用)。
    protected SalesCoreDbContext CreateAnotherSession(params IInterceptor[] interceptors) => CreateAnotherSession(null, interceptors);

    /// <summary>操作した人の出どころ(API の利用者など)を渡すと、保存の時に作成者・更新者・監査ログへ入る。</summary>
    protected SalesCoreDbContext CreateAnotherSession(ICurrentUserSource? currentUserSource, params IInterceptor[] interceptors)
    {
        var other = new SalesCoreDbContext(
            new DbContextOptionsBuilder<SalesCoreDbContext>()
                .UseNpgsql(Db.Database.GetDbConnection())
                .AddInterceptors(interceptors)
                .Options,
            currentUserSource);
        other.Database.UseTransaction(_transaction!.GetDbTransaction());
        return other;
    }

    /// <summary>
    /// ユースケースに渡す DbContext の工場。操作ごとに、このテストのトランザクションに入った2人目を作る(本番と同じく操作ごとに作って捨てる)。
    /// </summary>
    protected IDbContextFactory<SalesCoreDbContext> Sessions(ICurrentUserSource? currentUserSource = null) =>
        new TestSessions(() => CreateAnotherSession(currentUserSource));

    /// <summary>
    /// 別に作った DbContext(Identity など)を、このテストのトランザクションに入れる。書いたデータはテストの終わりに一緒に取り消される。
    /// 接続も同じものを使うこと(<c>UseNpgsql(Db.Database.GetDbConnection())</c>)。
    /// </summary>
    protected void JoinTransaction(DbContext context) => context.Database.UseTransaction(_transaction!.GetDbTransaction());

    public async Task DisposeAsync()
    {
        if (_transaction is not null)
        {
            await _transaction.DisposeAsync(); // コミットしていないので、ここで取り消される
        }
        await Db.DisposeAsync();
    }
}
