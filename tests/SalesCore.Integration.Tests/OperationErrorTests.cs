using Microsoft.EntityFrameworkCore;
using SalesCore.Application;
using SalesCore.Domain;

namespace SalesCore.Integration.Tests;

/// <summary>
/// 操作の誤りの扱い(API の状態コードと、画面に出す理由)。業務の誤りだけ理由を見せ、それ以外は中身を見せない。
/// </summary>
public class OperationErrorTests(DatabaseFixture fixture) : DatabaseTest(fixture)
{
    public static TheoryData<Exception, int> BusinessErrors => new()
    {
        { new UnauthenticatedException(), 401 },
        { new ForbiddenException("役割が足りない。"), 403 },
        { new NotFoundException("受注 1 が無い。"), 404 },
        { new InvalidInputException("数量は0より大きくする(0)。"), 400 },
        { new BusinessRuleException("状態が Cancelled の受注は出荷できない。"), 409 },
    };

    [Theory]
    [MemberData(nameof(BusinessErrors))]
    public void 業務の誤りは_状態コードと理由になる(Exception exception, int status)
    {
        Assert.Equal(new OperationError(status, exception.Message), OperationError.From(exception));
    }

    [Fact]
    public void 同時の変更は409で_読み直しを促す()
    {
        var error = OperationError.From(new DbUpdateConcurrencyException("内部の情報"))!;

        Assert.Equal(409, error.Status);
        Assert.Contains("読み直して", error.Message);
        Assert.DoesNotContain("内部", error.Message);
    }

    // 標準の例外は EF Core や LINQ も投げ、内部のメッセージが見えるので、扱わない(API は 500、画面は決まった文言)
    [Fact]
    public void 業務の誤りでない例外は_扱わない()
    {
        Assert.Null(OperationError.From(new InvalidOperationException("内部の情報")));
        Assert.Null(OperationError.From(new ArgumentException("内部の情報")));
    }

    // 一意インデックスを足したのに理由を書き忘れると、汎用の文言になる。DB のすべての一意インデックスに理由があることを確かめる
    [Fact]
    public void DBの一意インデックスには_すべて制約ごとの理由がある()
    {
        var uniqueIndexes = Db.Model.GetEntityTypes()
            .SelectMany(type => type.GetIndexes())
            .Where(index => index.IsUnique)
            .Select(index => index.GetDatabaseName()!)
            .Order()
            .ToList();

        Assert.NotEmpty(uniqueIndexes);
        Assert.Equal(uniqueIndexes, OperationError.UniqueConstraints.Keys.Order());
    }
}
