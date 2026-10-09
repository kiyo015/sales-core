using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
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
        new() { "SalesCore.Domain", "SalesCore.Application", "SalesCore.Infrastructure", "SalesCore.Api" };

    // EF Core で行を消す呼び出し。DbContext.Remove・DbSet<T>.Remove・RemoveRange・ExecuteDelete(Async)
    private static readonly HashSet<string> DeleteMethods = ["Remove", "RemoveRange", "ExecuteDelete", "ExecuteDeleteAsync"];

    // ExecuteSqlRaw などに書いた SQL。マイグレーションの Down() は MigrationBuilder の呼び出しなので、ここには入らない
    private static readonly Regex DeleteSql = new(@"\b(DELETE\s+FROM|TRUNCATE)\b", RegexOptions.IgnoreCase);

    [Theory]
    [MemberData(nameof(SourceAssemblies))]
    public void EFCoreで行を消す呼び出しが無い(string assemblyName)
    {
        var calls = ReadAssembly(assemblyName, metadata => metadata.MemberReferences
            .Select(metadata.GetMemberReference)
            .Where(member => DeleteMethods.Contains(metadata.GetString(member.Name)))
            .Select(member => $"{ParentTypeName(metadata, member.Parent)}.{metadata.GetString(member.Name)}")
            .Where(call => call.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal))
            .Distinct()
            .ToList());

        Assert.Empty(calls);
    }

    [Theory]
    [MemberData(nameof(SourceAssemblies))]
    public void 行を消すSQLが無い(string assemblyName)
    {
        var sql = ReadAssembly(assemblyName, metadata => UserStrings(metadata).Where(s => DeleteSql.IsMatch(s)).ToList());

        Assert.Empty(sql);
    }

    // 調べる仕組みそのものが働いていることの確認。テスト自身の DLL には「DELETE FROM」の文字列があるので、見つからなければ読み方が誤っている
    [Fact]
    public void 調べ方の確認_文字列はDLLから読める()
    {
        const string probe = "DELETE FROM probe_table";
        var found = ReadAssembly("SalesCore.Architecture.Tests", metadata => UserStrings(metadata).Contains(probe));

        Assert.True(found);
    }

    private static T ReadAssembly<T>(string assemblyName, Func<MetadataReader, T> read)
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll"));
        using var pe = new PEReader(stream);
        return read(pe.GetMetadataReader());
    }

    // ExecuteSqlRaw("...") などに渡した文字列は、DLL の「ユーザー文字列」領域に入る
    private static IEnumerable<string> UserStrings(MetadataReader metadata)
    {
        var handle = MetadataTokens.UserStringHandle(1);
        while (!handle.IsNil)
        {
            yield return metadata.GetUserString(handle);
            handle = metadata.GetNextHandle(handle);
        }
    }

    // 呼び出し先の型名。DbSet<T> のようなジェネリック型は、型引数を外した定義の名前にする
    private static string ParentTypeName(MetadataReader metadata, EntityHandle parent)
    {
        switch (parent.Kind)
        {
            case HandleKind.TypeReference:
                var reference = metadata.GetTypeReference((TypeReferenceHandle)parent);
                return $"{metadata.GetString(reference.Namespace)}.{metadata.GetString(reference.Name)}";
            case HandleKind.TypeSpecification:
                // ジェネリック型のインスタンス: 0x15(GENERICINST) 0x12/0x11(CLASS/VALUETYPE) <型の参照> …
                var blob = metadata.GetBlobReader(metadata.GetTypeSpecification((TypeSpecificationHandle)parent).Signature);
                return blob.ReadByte() == 0x15 && blob.ReadByte() is 0x12 or 0x11
                    ? ParentTypeName(metadata, blob.ReadTypeHandle())
                    : "";
            default:
                return "";
        }
    }
}
