using Microsoft.EntityFrameworkCore;
using Npgsql;
using SalesCore.Domain;
using SalesCore.Infrastructure.Persistence;

namespace SalesCore.Integration.Tests;

/// <summary>
/// 入金の表(receipts・receipt_allocations と invoices.allocated_amount。マイグレーション AddReceipts)。
/// 保存して読み直しても割当と割当済の額が変わらないこと、同時に充てる・取り消すと後の方が止まること、
/// 割当の合計の決まりを DB に直接書いても止まること。日付は書いた日・実行する日と重ならない 2030 年にする。
/// </summary>
public class ReceiptPersistenceTests(DatabaseFixture fixture) : DatabaseTest(fixture)
{
    private static readonly DateOnly Today = new(2030, 12, 10);
    private static readonly DateOnly ReceivedOn = new(2030, 12, 5);

    // 10月締め 1,155円(105円×10＋税105円)と、11月締め 2,310円(105円×20＋税210円)。
    // 11月を先に作り、Id の順と締め日の順を逆にする(Id の順に充てる誤りが見えるように)
    private async Task<(long October, long November)> AddInvoicesAsync()
    {
        var scenario = new TestScenario(Db);
        var m = await scenario.SeedMastersAsync();
        var november = await scenario.AddInvoiceAsync(m, new DateOnly(2030, 11, 30), 20m);
        var october = await scenario.AddInvoiceAsync(m, new DateOnly(2030, 10, 31), 10m);
        await scenario.EndRequestAsync();
        return (october.Id, november.Id);
    }

    // ユースケースの代わり: 請求先の請求書を全部読んで渡す(未回収の選び方と順は Domain が決める)。
    // C003(別の会社)には請求書が無いので、入金はすべて前受になる
    private static async Task<Receipt> RegisterAsync(SalesCoreDbContext db, decimal yen, string customerCode = "C001")
    {
        var customer = await db.Customers.SingleAsync(c => c.Code == customerCode);
        var invoices = await db.Invoices.Where(i => EF.Property<long>(i, "BillingCustomerId") == customer.Id).ToListAsync();
        var receipt = Receipt.Register(customer, ReceivedOn, Money.Of(yen), invoices, Today);
        db.Add(receipt);
        return receipt;
    }

    private static Task<Receipt> ReadAsync(SalesCoreDbContext db, long id) =>
        db.Receipts.Include(r => r.BillingCustomer).Include(r => r.Allocations).ThenInclude(a => a.Invoice).SingleAsync(r => r.Id == id);

    private async Task<decimal> InvoiceAllocatedAsync(long id) =>
        (await Db.Invoices.AsNoTracking().SingleAsync(i => i.Id == id)).AllocatedAmount.Yen;

    [Fact]
    public async Task 入金を保存して読み直すと_割当と割当済の額が戻り_番号が振られる()
    {
        var (october, november) = await AddInvoicesAsync();
        var saved = await RegisterAsync(Db, 1500m);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();

        var receipt = await ReadAsync(Db, saved.Id);

        Assert.Equal(("C001", ReceivedOn, 1500m, 1500m, false), (receipt.BillingCustomer.Code, receipt.ReceivedOn, receipt.Amount.Yen, receipt.AllocatedAmount.Yen, receipt.IsCancelled));
        Assert.Equal([(october, 1155m), (november, 345m)], receipt.Allocations.OrderBy(a => a.Id).Select(a => (a.Invoice.Id, a.Amount.Yen)));
        Assert.Equal((1155m, 345m), (await InvoiceAllocatedAsync(october), await InvoiceAllocatedAsync(november)));
        Assert.Matches(@"^RC-\d{6}$", (string)Db.Entry(receipt).Property("Number").CurrentValue!);
    }

    [Fact]
    public async Task 取り消した入金を読み直すと_打ち消しの入金と割当が残り_請求書は未回収に戻る()
    {
        var (october, november) = await AddInvoicesAsync();
        var saved = await RegisterAsync(Db, 5000m);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();

        var reversal = (await ReadAsync(Db, saved.Id)).Cancel(Today);
        Db.Add(reversal);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();

        var original = await ReadAsync(Db, saved.Id);
        var read = await ReadAsync(Db, reversal.Id);
        Assert.True(original.IsCancelled);
        Assert.Equal((Today, -5000m, -3465m, saved.Id), (read.ReceivedOn, read.Amount.Yen, read.AllocatedAmount.Yen, read.ReversesId));
        Assert.Equal([(october, -1155m), (november, -2310m)], read.Allocations.OrderBy(a => a.Id).Select(a => (a.Invoice.Id, a.Amount.Yen)));
        Assert.Equal((0m, 0m), (await InvoiceAllocatedAsync(october), await InvoiceAllocatedAsync(november)));
    }

    // 入金と請求書に持つ割当済の額が、割当の行の合計と合うこと(持ち方を2つにしたので、食い違いを SQL で確かめる)
    [Fact]
    public async Task 入金と請求書の割当済の額は_割当の行の合計と合う()
    {
        await AddInvoicesAsync();
        var first = await RegisterAsync(Db, 1000m);
        await Db.SaveChangesAsync();
        await RegisterAsync(Db, 2000m);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        Db.Add((await ReadAsync(Db, first.Id)).Cancel(Today));
        await Db.SaveChangesAsync();

        var receiptGaps = await Db.Database.SqlQuery<long>($"""
            SELECT r.id AS "Value" FROM receipts r
            WHERE r.allocated_amount <> (SELECT coalesce(sum(a.amount), 0) FROM receipt_allocations a WHERE a.receipt_id = r.id)
            """).ToListAsync();
        var invoiceGaps = await Db.Database.SqlQuery<long>($"""
            SELECT i.id AS "Value" FROM invoices i
            WHERE i.allocated_amount <> (SELECT coalesce(sum(a.amount), 0) FROM receipt_allocations a WHERE a.invoice_id = i.id)
            """).ToListAsync();
        Assert.Equal(3, await Db.Receipts.CountAsync());
        Assert.Empty(receiptGaps);
        Assert.Empty(invoiceGaps);
    }

    // 同じ請求書の未回収を2人が同時に読み、それぞれ入金を充てた。請求書の行を更新するので、後の方は版数で止まる
    // (止まらないと、2人とも残り全額を充てて、今回分を超えて消し込む)
    [Fact]
    public async Task 同じ請求書に2人が同時に入金を充てると_後の方は止まる()
    {
        await AddInvoicesAsync();
        await using var other = CreateAnotherSession();
        await RegisterAsync(other, 1155m);
        await RegisterAsync(Db, 1155m);

        await other.SaveChangesAsync();

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => Db.SaveChangesAsync());
    }

    // 入金の守りは2つ: 打ち消しの入金が元の入金を指す一意インデックス(IX_receipts_reverses_id)と、元の入金の版数(取り消した印を立てる)。
    // 保存は打ち消しの行の INSERT が先に走るので、両方あれば一意インデックスが先に止める(23505)。どちらか片方でも止まることは対照実験で確かめた。
    // 請求書に充てた入金なら、取消で請求書の行も更新するので請求書の版数でも止まる。入金の守りだけを確かめるため、前受だけの入金にする
    [Fact]
    public async Task 前受だけの入金を2人が同時に取り消すと_後の方は止まる()
    {
        await AddInvoicesAsync();
        var saved = await RegisterAsync(Db, 1000m, "C003");
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        await using var other = CreateAnotherSession();
        var mine = await ReadAsync(Db, saved.Id);
        var theirs = await ReadAsync(other, saved.Id);

        other.Add(theirs.Cancel(Today));
        await other.SaveChangesAsync();
        Db.Add(mine.Cancel(Today));

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => Db.SaveChangesAsync()); // DbUpdateConcurrencyException も含む
        Assert.Equal(1, await other.Receipts.CountAsync(r => r.ReversesId == saved.Id));
    }

    [Fact]
    public async Task 割当を読み込まずに取り消そうとすると_止まる()
    {
        await AddInvoicesAsync();
        var saved = await RegisterAsync(Db, 1000m);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();

        var withoutAllocations = await Db.Receipts.Include(r => r.BillingCustomer).SingleAsync(r => r.Id == saved.Id);

        Assert.Contains("割当を読み込んでいない", Assert.Throws<InvalidOperationException>(() => withoutAllocations.Cancel(Today)).Message);
    }

    // 割当の請求書(ThenInclude)や入金の請求先を読み込み忘れても、どの請求書も書き換えずに理由つきで止まる
    // (止めないと、途中の請求書まで消し込んだ額を戻してから NullReferenceException になる。PR #28 の AIレビュー)
    [Fact]
    public async Task 割当の請求書や請求先を読み込まずに取り消そうとすると_何も変えずに止まる()
    {
        await AddInvoicesAsync();
        var saved = await RegisterAsync(Db, 1500m);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();

        var withoutInvoices = await Db.Receipts.Include(r => r.BillingCustomer).Include(r => r.Allocations).SingleAsync(r => r.Id == saved.Id);
        Assert.Contains("割当の請求書を読み込んでいない", Assert.Throws<InvalidOperationException>(() => withoutInvoices.Cancel(Today)).Message);
        Db.ChangeTracker.Clear();

        var withoutCustomer = await Db.Receipts.Include(r => r.Allocations).ThenInclude(a => a.Invoice).SingleAsync(r => r.Id == saved.Id);
        Assert.Contains("請求先を読み込んでいない", Assert.Throws<InvalidOperationException>(() => withoutCustomer.Cancel(Today)).Message);
        Assert.Equal([1155m, 345m], withoutCustomer.Allocations.OrderBy(a => a.Id).Select(a => a.Invoice.AllocatedAmount.Yen));
        Assert.False(withoutCustomer.IsCancelled);
    }

    // ── DB の検査制約。ドメインを通さずに SQL で直接書いても止まる ──

    [Theory]
    [InlineData(1156)] // 今回分 1,155円を1円超える
    [InlineData(-1)]
    public async Task 請求書の消し込んだ額は_DBに直接書いても0以上_今回分以下(decimal allocated)
    {
        var (october, _) = await AddInvoicesAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => Db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE invoices SET allocated_amount = {allocated} WHERE id = {october}"));

        Assert.Equal((PostgresErrorCodes.CheckViolation, "ck_invoices_allocated"), (error.SqlState, error.ConstraintName));
    }

    [Theory]
    [InlineData(1001)] // 入金額 1,000円を1円超える
    [InlineData(-1)] // 入金額と符号が違う
    public async Task 入金の割当済の額は_DBに直接書いても入金額を超えず_符号も同じ(decimal allocated)
    {
        await AddInvoicesAsync();
        var saved = await RegisterAsync(Db, 1000m);
        await Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => Db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE receipts SET allocated_amount = {allocated} WHERE id = {saved.Id}"));

        Assert.Equal((PostgresErrorCodes.CheckViolation, "ck_receipts_allocated"), (error.SqlState, error.ConstraintName));
    }

    [Fact]
    public async Task 打ち消しの入金は_DBに直接つないでも同じ入金を2つ指せない()
    {
        await AddInvoicesAsync();
        var saved = await RegisterAsync(Db, 1000m);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        var reversal = (await ReadAsync(Db, saved.Id)).Cancel(Today);
        Db.Add(reversal);
        await Db.SaveChangesAsync();
        var another = await RegisterAsync(Db, 1m);
        await Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => Db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE receipts SET reverses_id = {saved.Id} WHERE id = {another.Id}"));

        Assert.Equal((PostgresErrorCodes.UniqueViolation, "IX_receipts_reverses_id"), (error.SqlState, error.ConstraintName));
    }
}
