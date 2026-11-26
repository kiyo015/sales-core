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

    // 入力の桁は Domain が理由つきで止める(明細の数量・単価・金額、マスタの単価)。DB で桁あふれになるのは入力でない値(集計など)なので、
    // 「入力した数が大きすぎる」と利用者のせいにしない(Day42 からの持ち越し。API は 500、画面は決まった文言)
    [Fact]
    public void DBの桁あふれは_入力の誤りにしない()
    {
        var overflow = new DbUpdateException("内部の情報", new Npgsql.PostgresException("numeric field overflow", "ERROR", "ERROR", Npgsql.PostgresErrorCodes.NumericValueOutOfRange));

        Assert.Null(OperationError.From(overflow));
    }

    // 月次締めの表のロックと、書く側の順番が逆になると PostgreSQL がデッドロックで片方を止める(40P01)。理由の無い 500 にせず、
    // やり直せばよい 409 にする(保存の時は DbUpdateException の内側、月次締めのロックの時は PostgresException のまま。PR #31 の AIレビュー)
    [Fact]
    public void デッドロックで止められたら_やり直す409にする()
    {
        var deadlock = new Npgsql.PostgresException("deadlock detected", "ERROR", "ERROR", Npgsql.PostgresErrorCodes.DeadlockDetected);
        var expected = new OperationError(409, "ほかの操作と重なった。やり直す。");

        Assert.Equal(expected, OperationError.From(deadlock));
        Assert.Equal(expected, OperationError.From(new DbUpdateException("内部の情報", deadlock)));
    }

    // 排他制約の違反は、得意先別単価の制約だけ「単価と期間が重なる」にする。税率の重なりを単価の理由にしない(PR #24 の AIレビュー)
    [Fact]
    public async Task 税率の期間の重なりは_単価の理由にしない()
    {
        var standard = await Db.TaxCategories.SingleAsync(t => t.Code == "STD");
        Db.Add(new TaxRatePeriod(standard, TaxRate.Of(12m), new DateOnly(2030, 1, 1))); // 2019-10-01 からの 10% と重なる

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => Db.SaveChangesAsync());

        Assert.Equal("ex_tax_rates_no_overlap", ((Npgsql.PostgresException)error.InnerException!).ConstraintName);
        Assert.Null(OperationError.From(error));
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
