using Microsoft.EntityFrameworkCore;
using SalesCore.Domain;
using SalesCore.Infrastructure.Persistence;

namespace SalesCore.Application;

/// <summary>
/// 月次締め(業務ルール集「4. 請求」の「月次締め」)。締められるか・残高の計算は Domain(<see cref="MonthlyClosing.Close"/>)が決め、
/// ここでは前の月の締めと、その月の売上・請求書・入金を集めて渡す。「今日」(締められるのは終わった月だけ)はサーバーの時計の日本時間。
/// </summary>
public sealed class MonthlyClosingUseCases(IDbContextFactory<SalesCoreDbContext> sessions, TimeProvider clock, ICurrentUser user)
{
    public async Task<MonthlyClosingView> CloseAsync(int year, int month)
    {
        Permissions.Require(user, Operation.CloseMonth);
        await using var db = await sessions.CreateDbContextAsync();
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync() : null;
        // 締めている間は、売上・入金・請求書を書かせない(書き終わるのを待ってから読む)。月次締めどうしも1つずつ。
        // 書く側が先に月次締めの行を読んで通っても、この後に保存しようとすれば DB のトリガー(締めた月の日付を止める)が止める。
        // ponytail: 表ごとロックする。締めは月に1回で短いので、書く側を少し待たせるだけ。書く側と順番が逆になってデッドロックしたら、PostgreSQL が片方を止める(やり直せばよい)
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE monthly_closings IN EXCLUSIVE MODE; LOCK TABLE sales_records, receipts, invoices IN SHARE MODE");

        var previous = await db.MonthlyClosings.Include(m => m.Balances).ThenInclude(b => b.BillingCustomer).OrderByDescending(m => m.Month).FirstOrDefaultAsync();
        var first = new DateOnly(Math.Clamp(year, 1, 9998), Math.Clamp(month, 1, 12), 1); // 範囲外の年月は Domain が理由をつけて止める
        var last = first.AddMonths(1).AddDays(-1);
        DateOnly? firstTransaction = previous is not null ? null : await FirstTransactionAsync(db);
        // その月の売上と、月末までの未請求の売上(締め忘れの検査)。請求先は同じ DbContext で読むので、同じ請求先は同じオブジェクトになる
        var sales = await db.SalesRecords.Include(r => r.Lines).Include(r => r.BillingCustomer)
            .Where(r => r.RecordedOn <= last && (r.RecordedOn >= first || r.InvoiceId == null)).ToListAsync();
        // ponytail: 請求書を全部読む(締め忘れの検査に、その月以後の締め日の請求書も使う)。請求書は請求先ごとに月1件なので、多くなったら締め日で絞る
        var invoices = await db.Invoices.Include(i => i.BillingCustomer).ToListAsync();
        var receipts = await db.Receipts.Include(r => r.BillingCustomer).Where(r => r.ReceivedOn >= first && r.ReceivedOn <= last).ToListAsync();

        var closing = MonthlyClosing.Close(year, month, clock.Today(), previous, firstTransaction, sales, invoices, receipts);
        db.Add(closing);
        await db.SaveChangesAsync();
        if (transaction is not null) await transaction.CommitAsync();
        return View(await db.MonthlyClosings.AsNoTracking().Include(m => m.Balances).ThenInclude(b => b.BillingCustomer).SingleAsync(m => m.Id == closing.Id));
    }

    /// <summary>締めた月の一覧(新しい順)。期末は請求先の期末の合計。</summary>
    // ponytail: 全部の月の残高を読んで足す(月に請求先の数の行)。年が重なって重くなったら、合計を DB で出す
    public async Task<IReadOnlyList<MonthlyClosingSummary>> ListAsync()
    {
        Permissions.Require(user, Operation.ViewReceivableBalance);
        await using var db = await sessions.CreateDbContextAsync();
        var closings = await db.MonthlyClosings.AsNoTracking().Include(m => m.Balances).OrderByDescending(m => m.Month).ToListAsync();
        return closings.Select(m => new MonthlyClosingSummary(m.Month.Year, m.Month.Month, m.Balances.Sum(b => b.Closing.Yen))).ToList();
    }

    /// <summary>締めた月の請求先ごとの残高。締めていない月は出さない(2026-11-27 本人の決定)。</summary>
    public async Task<MonthlyClosingView> GetAsync(int year, int month)
    {
        Permissions.Require(user, Operation.ViewReceivableBalance);
        await using var db = await sessions.CreateDbContextAsync();
        var first = year is >= 1 and <= 9999 && month is >= 1 and <= 12 ? new DateOnly(year, month, 1) : (DateOnly?)null;
        var closing = first is null ? null : await db.MonthlyClosings.AsNoTracking()
            .Include(m => m.Balances).ThenInclude(b => b.BillingCustomer).SingleOrDefaultAsync(m => m.Month == first);
        return closing is null ? throw new NotFoundException($"{year}年{month}月は月次締めしていない。") : View(closing);
    }

    /// <summary>請求先の残高の推移(締めた月の古い順)。支店の売上は請求先の残高に入るので、支店は請求先として探さない。</summary>
    // ponytail: 全部の月の残高を読んで、その請求先の行を選ぶ(画面の推移と同じく、件数が増えたら DB で絞る)
    public async Task<ReceivableHistoryView> GetHistoryAsync(string billingCustomerCode)
    {
        Permissions.Require(user, Operation.ViewReceivableBalance);
        await using var db = await sessions.CreateDbContextAsync();
        var customer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(c => c.Code == billingCustomerCode && c.BillingCustomerId == null)
            ?? throw new NotFoundException($"請求先 {billingCustomerCode} は無い(支店の残高は請求先の残高に入る)。");
        var closings = await db.MonthlyClosings.AsNoTracking().Include(m => m.Balances).ThenInclude(b => b.BillingCustomer).OrderBy(m => m.Month).ToListAsync();
        var months = closings
            .SelectMany(m => m.Balances.Where(b => b.BillingCustomer.Code == customer.Code)
                .Select(b => new MonthlyBalanceView(m.Month.Year, m.Month.Month, b.Opening.Yen, b.Sales.Yen, b.Tax.Yen, b.Received.Yen, b.Closing.Yen)))
            .ToList();
        return new ReceivableHistoryView(customer.Code, customer.Name, months);
    }

    /// <summary>
    /// 次に締める月(月次締めの画面の初期値)。締めた月の翌月、まだ締めていなければ取引の一番古い月。取引が無ければ null。
    /// 締める人が使う問い合わせなので、締めと同じ行(経理と管理者)で検査する。
    /// </summary>
    public async Task<NextClosingView?> NextAsync()
    {
        Permissions.Require(user, Operation.CloseMonth);
        await using var db = await sessions.CreateDbContextAsync();
        var next = (await LatestAsync(db))?.Month.AddMonths(1) ?? FirstDayOfMonth(await FirstTransactionAsync(db));
        return next is { } first ? new NextClosingView(first.Year, first.Month, first.AddMonths(1).AddDays(-1) < clock.Today()) : null;
    }

    // 請求書の締め日は見ない(請求書は売上からしか作れず、締め日はその売上の売上日以後なので、一番古い日付にならない)
    private static async Task<DateOnly?> FirstTransactionAsync(SalesCoreDbContext db) => Min(
        await db.SalesRecords.MinAsync(r => (DateOnly?)r.RecordedOn), await db.Receipts.MinAsync(r => (DateOnly?)r.ReceivedOn));

    /// <summary>一番新しい月次締め(残高は読まない)。締めた月の日付を止める <see cref="MonthlyClosing.EnsureOpen"/> に渡す。</summary>
    internal static Task<MonthlyClosing?> LatestAsync(SalesCoreDbContext db) =>
        db.MonthlyClosings.AsNoTracking().OrderByDescending(m => m.Month).FirstOrDefaultAsync();

    private static DateOnly? FirstDayOfMonth(DateOnly? date) => date?.AddDays(1 - date.Value.Day);

    private static DateOnly? Min(DateOnly? a, DateOnly? b) => a is null ? b : b is null ? a : a < b ? a : b;

    private static MonthlyClosingView View(MonthlyClosing closing) =>
        new(closing.Month.Year, closing.Month.Month, closing.Balances
            .OrderBy(b => b.BillingCustomer.Code, StringComparer.Ordinal) // DB から読んだ子の行は順序が決まらない
            .Select(b => new ReceivableBalanceView(b.BillingCustomer.Code, b.BillingCustomer.Name, b.Opening.Yen, b.Sales.Yen, b.Tax.Yen, b.Received.Yen, b.Closing.Yen))
            .ToList());
}
