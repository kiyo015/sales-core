using Microsoft.EntityFrameworkCore;
using Npgsql;
using SalesCore.Domain;

namespace SalesCore.Integration.Tests;

/// <summary>
/// 見積の表(quotations・quotation_lines。マイグレーション AddQuotations)。保存して読み直しても、状態・明細・金額・税が変わらないこと。
/// 読み直しは ChangeTracker.Clear() の後に行う。日付は書いた日・実行する日と重ならない 2030 年にする。
/// </summary>
public class QuotationPersistenceTests(DatabaseFixture fixture) : DatabaseTest(fixture)
{
    private static readonly DateOnly QuotedOn = new(2030, 4, 1);
    private static readonly DateOnly ValidUntil = new(2030, 4, 30);

    private async Task<Quotation> AddQuotationAsync()
    {
        var masters = await new TestScenario(Db).SeedMastersAsync();
        // 105円×3(10%)と 33.33円×1.5(8%)。税は税率ごとに1回丸める(315円→32円、49.995→50円→4円)
        var lines = new[]
        {
            (Line: new QuotationLine(3m, 105m, TaxRate.Of(10m)), Product: masters.StandardProduct),
            (Line: new QuotationLine(1.5m, 33.33m, TaxRate.Of(8m)), Product: masters.ReducedProduct),
        };
        var quotation = new Quotation(masters.HeadOffice, QuotedOn, ValidUntil, lines.Select(l => l.Line));
        Db.Add(quotation);
        foreach (var (line, product) in lines)
        {
            Db.Entry(line).Property("ProductId").CurrentValue = product.Id;
        }
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return quotation;
    }

    private Task<Quotation> ReadAsync(long id) =>
        Db.Quotations.Include(q => q.Customer).Include(q => q.Lines).SingleAsync(q => q.Id == id);

    [Fact]
    public async Task 見積を保存して読み直すと_明細と金額と税が戻り_番号が振られる()
    {
        var saved = await AddQuotationAsync();

        var quotation = await ReadAsync(saved.Id);

        Assert.Equal(("C001", QuotedOn, ValidUntil, QuotationStatus.Draft), (quotation.Customer.Code, quotation.QuotedOn, quotation.ValidUntil, quotation.Status));
        Assert.Equal(
            [(3m, 105m, TaxRate.Of(10m)), (1.5m, 33.33m, TaxRate.Of(8m))],
            quotation.Lines.OrderBy(l => l.Id).Select(l => (l.Quantity, l.UnitPrice, l.TaxRate)));
        Assert.Equal((Money.Of(365m), Money.Of(36m)), (quotation.Amount, quotation.Tax.TaxTotal));
        Assert.Matches(@"^QT-\d{6}$", (string)Db.Entry(quotation).Property("Number").CurrentValue!);
    }

    [Fact]
    public async Task 直した明細と進めた状態は_読み直しても残る()
    {
        var saved = await AddQuotationAsync();
        var quotation = await ReadAsync(saved.Id);
        var line = quotation.Lines.Single(l => l.UnitPrice == 105m);

        quotation.ChangeLine(line, 2m, 100m);
        quotation.Submit();
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();

        var read = await ReadAsync(saved.Id);
        Assert.Equal(QuotationStatus.Submitted, read.Status);
        Assert.Equal((2m, 100m), read.Lines.Where(l => l.Id == line.Id).Select(l => (l.Quantity, l.UnitPrice)).Single());
    }

    // 同じ見積を2人が開いて、1人が提出・もう1人が失注にした。後の方は版数(楽観ロック)で止まる
    [Fact]
    public async Task 同じ見積を2人が同時に進めると_後の方は止まる()
    {
        var saved = await AddQuotationAsync();
        await using var other = CreateAnotherSession();
        var mine = await ReadAsync(saved.Id);
        var theirs = await other.Quotations.SingleAsync(q => q.Id == saved.Id);

        theirs.Submit();
        await other.SaveChangesAsync();
        mine.MarkLost();

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => Db.SaveChangesAsync());
    }

    // ドメインを通さずに SQL で直接入れても、DB が止める
    [Fact]
    public async Task 有効期限が見積日より前の見積は_DBに直接入れようとしても入らない()
    {
        var saved = await AddQuotationAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => Db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE quotations SET valid_until = quoted_on - 1 WHERE id = {saved.Id}"));

        Assert.Equal((PostgresErrorCodes.CheckViolation, "ck_quotations_valid_period"), (error.SqlState, error.ConstraintName));
    }
}
