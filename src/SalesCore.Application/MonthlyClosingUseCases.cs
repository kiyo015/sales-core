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
        DateOnly? firstTransaction = previous is not null ? null : Min(
            await db.SalesRecords.MinAsync(r => (DateOnly?)r.RecordedOn), await db.Receipts.MinAsync(r => (DateOnly?)r.ReceivedOn));
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

    /// <summary>一番新しい月次締め(残高は読まない)。締めた月の日付を止める <see cref="MonthlyClosing.EnsureOpen"/> に渡す。</summary>
    internal static Task<MonthlyClosing?> LatestAsync(SalesCoreDbContext db) =>
        db.MonthlyClosings.AsNoTracking().OrderByDescending(m => m.Month).FirstOrDefaultAsync();

    private static DateOnly? Min(DateOnly? a, DateOnly? b) => a is null ? b : b is null ? a : a < b ? a : b;

    private static MonthlyClosingView View(MonthlyClosing closing) =>
        new(closing.Month.Year, closing.Month.Month, closing.Balances
            .OrderBy(b => b.BillingCustomer.Code, StringComparer.Ordinal) // DB から読んだ子の行は順序が決まらない
            .Select(b => new ReceivableBalanceView(b.BillingCustomer.Code, b.Opening.Yen, b.Sales.Yen, b.Tax.Yen, b.Received.Yen, b.Closing.Yen))
            .ToList());
}
