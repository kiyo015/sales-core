using Microsoft.Extensions.Configuration;
using Npgsql;

namespace SalesCore.Integration.Tests;

/// <summary>
/// 結合テスト用DBの接続文字列を扱う。
/// 手元では user-secrets の ConnectionStrings:SalesCoreTest、CIでは環境変数
/// ConnectionStrings__SalesCoreTest から読む。
/// </summary>
public static class TestDatabase
{
    private const string Key = "SalesCoreTest";

    public static string LoadConnectionString()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets(typeof(TestDatabase).Assembly, optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString(Key)
            ?? throw new InvalidOperationException(
                $"接続文字列 ConnectionStrings:{Key} が設定されていない。CLAUDE.md の「DBの準備」に従って user-secrets に設定すること。");
        EnsureTestDatabase(connectionString);
        return connectionString;
    }

    /// <summary>
    /// DB名が <c>_test</c> で終わらなければ止める。結合テストはデータを書いては戻すので、
    /// 開発用DBを指していると、戻し損ねた時に開発用のデータを壊す。
    /// </summary>
    public static void EnsureTestDatabase(string connectionString)
    {
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database;
        if (database is null || !database.EndsWith("_test", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"結合テストの接続先がテスト用DBではない(Database={database ?? "未指定"})。DB名が _test で終わるDBだけを使う。");
        }
    }
}
