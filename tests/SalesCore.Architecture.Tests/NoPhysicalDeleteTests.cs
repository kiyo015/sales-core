using System.Text.RegularExpressions;

namespace SalesCore.Architecture.Tests;

/// <summary>
/// 受注・売上・請求などのデータを物理削除しない(業務ルール集「7. データの扱い」)。取消は状態か赤黒で表す。
/// src の全プロジェクトのコンパイル済みのコード(DLL)を読み、削除につながる呼び出しと SQL が無いことを確かめる。
/// DB の外部キー(RESTRICT)は「子のある親」の削除しか止めないので、子の無い行を消すコードはここで止める。
/// </summary>
public class NoPhysicalDeleteTests
{
    public static TheoryData<string> SourceAssemblies =>
        new() { "SalesCore.Domain", "SalesCore.Application", "SalesCore.Infrastructure", "SalesCore.Api", "SalesCore.Web" };

    // EF Core で行を消す呼び出し。DbContext.Remove・DbSet<T>.Remove・RemoveRange・ExecuteDelete(Async)
    private static readonly HashSet<string> DeleteMethods = ["Remove", "RemoveRange", "ExecuteDelete", "ExecuteDeleteAsync"];

    // ExecuteSqlRaw などに書いた SQL。マイグレーションの Down() は MigrationBuilder の呼び出しなので、ここには入らない
    private static readonly Regex DeleteSql = new(@"\b(DELETE\s+FROM|TRUNCATE)\b", RegexOptions.IgnoreCase);

    [Theory]
    [MemberData(nameof(SourceAssemblies))]
    public void EFCoreで行を消す呼び出しが無い(string assemblyName)
    {
        var calls = AssemblyMetadata.Read(assemblyName, AssemblyMetadata.MemberCalls)
            .Where(call => DeleteMethods.Contains(call[(call.LastIndexOf('.') + 1)..]))
            .Where(call => call.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(calls);
    }

    [Theory]
    [MemberData(nameof(SourceAssemblies))]
    public void 行を消すSQLが無い(string assemblyName)
    {
        var sql = AssemblyMetadata.Read(assemblyName, metadata => AssemblyMetadata.UserStrings(metadata).Where(s => DeleteSql.IsMatch(s)).ToList());

        Assert.Empty(sql);
    }

    // 調べる仕組みそのものが働いていることの確認。テスト自身の DLL には「DELETE FROM」の文字列があるので、見つからなければ読み方が誤っている
    [Fact]
    public void 調べ方の確認_文字列はDLLから読める()
    {
        const string probe = "DELETE FROM probe_table";
        var found = AssemblyMetadata.Read("SalesCore.Architecture.Tests", metadata => AssemblyMetadata.UserStrings(metadata).Contains(probe));

        Assert.True(found);
    }
}
