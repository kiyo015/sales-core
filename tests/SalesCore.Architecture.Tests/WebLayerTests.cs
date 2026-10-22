using System.Reflection.Metadata;

namespace SalesCore.Architecture.Tests;

/// <summary>
/// 画面(SalesCore.Web)は薄く保つ(段階2の設計)。業務の判断と保存は Application のユースケースを通し、
/// 画面から Domain の操作(状態を変えるメソッドなど)や Infrastructure(DbContext)を直接使わない。
/// 権限の検査をユースケースの入口に置くので、画面が Domain を直接触ると検査を素通りできてしまう。
/// Web のコンパイル済みのコード(.razor から生成されたものを含む)を読んで確かめる。
/// </summary>
public class WebLayerTests
{
    private const string Web = "SalesCore.Web";

    [Fact]
    public void 画面はDomainとInfrastructureのメソッドやプロパティを直接使わない()
    {
        var used = ReadMemberCalls(Web)
            .Where(call => call.StartsWith("SalesCore.Domain.", StringComparison.Ordinal)
                || call.StartsWith("SalesCore.Infrastructure.", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(used);
    }

    [Fact]
    public void 画面はInfrastructureの型を参照しない()
    {
        // DbContext を注入(@inject)するだけでも型の参照になる。Domain の例外型を catch するのは許す(メソッドは呼ばない)
        var types = NoPhysicalDeleteTests.ReadAssembly(Web, metadata => metadata.TypeReferences
            .Select(metadata.GetTypeReference)
            .Select(type => $"{metadata.GetString(type.Namespace)}.{metadata.GetString(type.Name)}")
            .Where(name => name.StartsWith("SalesCore.Infrastructure.", StringComparison.Ordinal))
            .Distinct()
            .ToList());

        Assert.Empty(types);
    }

    // 調べる仕組みそのものが働いていることの確認。Web は Application の AddSalesCore を呼んでいるので、見つからなければ読み方が誤っている
    [Fact]
    public void 調べ方の確認_画面からApplicationへの呼び出しは見える()
    {
        Assert.Contains("SalesCore.Application.SalesCoreServices.AddSalesCore", ReadMemberCalls(Web));
    }

    private static List<string> ReadMemberCalls(string assemblyName) =>
        NoPhysicalDeleteTests.ReadAssembly(assemblyName, metadata => metadata.MemberReferences
            .Select(metadata.GetMemberReference)
            .Select(member => $"{NoPhysicalDeleteTests.ParentTypeName(metadata, member.Parent)}.{metadata.GetString(member.Name)}")
            .Distinct()
            .ToList());
}
