using Microsoft.EntityFrameworkCore;
using SalesCore.Application;

namespace SalesCore.Architecture.Tests;

/// <summary>
/// ユースケースは DbContext を直接受け取らず、工場(IDbContextFactory)から操作ごとに作る(本人の決定 2026-10-30)。
/// 画面ではユースケースが回線の間ずっと生きるので、直接受け取ると前の操作で読んだデータを持ち越す。
/// AddDbContextFactory は DbContext そのものも登録するので、直接受け取ってもビルドも DI も通る。ここで止める(PR #14 の AIレビューの Minor)。
/// </summary>
public class UseCaseDbContextTests
{
    private static List<Type> UseCases() => typeof(SalesOrderUseCases).Assembly.GetExportedTypes()
        .Where(type => type.Name.EndsWith("UseCases", StringComparison.Ordinal))
        .ToList();

    [Fact]
    public void ユースケースはDbContextを直接受け取らない()
    {
        var direct = UseCases()
            .SelectMany(type => type.GetConstructors().SelectMany(c => c.GetParameters())
                .Where(p => typeof(DbContext).IsAssignableFrom(p.ParameterType))
                .Select(p => $"{type.Name}({p.ParameterType.Name} {p.Name})"))
            .ToList();

        Assert.Empty(direct);
    }

    // 調べ方の確認。マスタのユースケースが見つかり、工場を受け取っていること
    [Fact]
    public void 調べ方の確認_ユースケースは工場を受け取っている()
    {
        Assert.Contains(UseCases(), type => type == typeof(MasterUseCases));
        Assert.Contains(typeof(MasterUseCases).GetConstructors().Single().GetParameters(),
            p => p.ParameterType.IsGenericType && p.ParameterType.GetGenericTypeDefinition() == typeof(IDbContextFactory<>));
    }
}
