using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace SalesCore.Architecture.Tests;

/// <summary>コンパイル済みのコード(DLL)を、読み込まずにメタデータだけ読む。アーキテクチャテストの共通の部品。</summary>
internal static class AssemblyMetadata
{
    /// <summary>テストの出力先にある DLL を開いて読む。</summary>
    public static T Read<T>(string assemblyName, Func<MetadataReader, T> read)
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll"));
        using var pe = new PEReader(stream);
        return read(pe.GetMetadataReader());
    }

    /// <summary>呼び出し先(メソッド・プロパティ)を「型名.メンバー名」で並べる。</summary>
    public static List<string> MemberCalls(MetadataReader metadata) => metadata.MemberReferences
        .Select(metadata.GetMemberReference)
        .Select(member => $"{ParentTypeName(metadata, member.Parent)}.{metadata.GetString(member.Name)}")
        .Distinct()
        .ToList();

    // ExecuteSqlRaw("...") などに渡した文字列は、DLL の「ユーザー文字列」領域に入る
    public static IEnumerable<string> UserStrings(MetadataReader metadata)
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
