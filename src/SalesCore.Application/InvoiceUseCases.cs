using Microsoft.EntityFrameworkCore;
using SalesCore.Domain;
using SalesCore.Infrastructure.Persistence;

namespace SalesCore.Application;

/// <summary>
/// 締めと請求書。請求書の金額(税率ごとに1回だけ丸める)と二重請求の防止は Domain(<see cref="Invoice.Create"/>)が行い、
/// ここでは請求先・締め日・期間を決めて、締める売上を集める(業務ルール集「4. 請求」)。
/// DbContext は操作ごとに作って捨てる(<see cref="SalesOrderUseCases"/> と同じ)。
/// </summary>
public sealed class InvoiceUseCases(IDbContextFactory<SalesCoreDbContext> sessions, TimeProvider clock, ICurrentUser user)
{
    // 日付は日本時間で決める(日本は夏時間が無いので、+9時間で固定)
    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(9)).DateTime);

    /// <summary>
    /// 請求先の締め日で締め、請求書を作る。請求先あての、締め日までの未請求の売上を全部入れる(2026-10-16 決定)。
    /// 期間は前回の締め日の翌日から今回の締め日まで(最初の締めは、前の月の締め日の翌日から)。
    /// </summary>
    public async Task<InvoiceView> CloseAsync(CloseInvoice request)
    {
        Permissions.Require(user, Operation.CloseInvoice);
        await using var db = await sessions.CreateDbContextAsync();
        // 無効にした得意先でも、残っている売上は請求するので、無効かどうかは見ない
        var customer = await db.Customers.Include(c => c.BillingCustomer).SingleOrDefaultAsync(c => c.Code == request.BillingCustomerCode)
            ?? throw new InvalidInputException($"得意先コード {request.BillingCustomerCode} の得意先が無い。");
        if (customer.BillingCustomer is { } billingCustomer)
        {
            throw new InvalidInputException($"{customer.Code} の請求先は {billingCustomer.Code}。請求書は請求先で締める。");
        }
        if (!customer.IsClosingDate(request.ClosingDate))
        {
            var closingDay = customer.ClosingDay == 31 ? "月末" : $"毎月{customer.ClosingDay}日";
            throw new InvalidInputException($"{customer.Code} の締め日は{closingDay}。{request.ClosingDate:yyyy-MM-dd} では締められない。");
        }
        // 段階1は請求書を取り消せないので、未来の日付で締めると、その日までの締めがすべてできなくなる(2026-10-16 の AIレビュー)
        if (request.ClosingDate > Today)
        {
            throw new InvalidInputException($"締め日 {request.ClosingDate:yyyy-MM-dd} がまだ来ていない(今日は {Today:yyyy-MM-dd})。締め日の当日から締められる。");
        }

        var lastClosingDate = await db.Invoices
            .Where(i => EF.Property<long>(i, "BillingCustomerId") == customer.Id)
            .OrderByDescending(i => i.ClosingDate)
            .Select(i => (DateOnly?)i.ClosingDate)
            .FirstOrDefaultAsync();
        if (lastClosingDate >= request.ClosingDate)
        {
            throw new BusinessRuleException($"{customer.Code} は {lastClosingDate:yyyy-MM-dd} まで締め済み。{request.ClosingDate:yyyy-MM-dd} では締められない。");
        }
        var periodFrom = (lastClosingDate ?? customer.PreviousClosingDate(request.ClosingDate)).AddDays(1);

        var records = await db.SalesRecords.Include(r => r.Lines)
            .Where(r => EF.Property<long>(r, "BillingCustomerId") == customer.Id && r.InvoiceId == null && r.RecordedOn <= request.ClosingDate)
            .ToListAsync();

        var invoice = Invoice.Create(periodFrom, request.ClosingDate, records);
        db.Add(invoice);
        db.Entry(invoice).Property("BillingCustomerId").CurrentValue = customer.Id;
        await db.SaveChangesAsync();
        return await ReadAsync(db, invoice.Id);
    }

    public async Task<InvoiceView> GetAsync(long id)
    {
        Permissions.Require(user, Operation.ViewInvoice);
        await using var db = await sessions.CreateDbContextAsync();
        return await ReadAsync(db, id);
    }

    // 締めの後の読み直しにも使うので、参照の検査はしない(検査は呼ぶ側の入口で済んでいる)
    private static async Task<InvoiceView> ReadAsync(SalesCoreDbContext db, long id)
    {
        var invoice = await db.Invoices.AsNoTracking().Include("_taxSummaries").SingleOrDefaultAsync(i => i.Id == id)
            ?? throw new NotFoundException($"請求書 {id} が無い。");
        var header = await (
                from i in db.Invoices
                join c in db.Customers on EF.Property<long>(i, "BillingCustomerId") equals c.Id
                where i.Id == id
                select new { Number = EF.Property<string>(i, "Number"), DueDate = EF.Property<DateOnly?>(i, "DueDate"), CustomerCode = c.Code })
            .SingleAsync();
        var recordNumbers = await db.SalesRecords.Where(r => r.Invoice != null && r.Invoice.Id == id)
            .OrderBy(r => r.Id)
            .Select(r => EF.Property<string>(r, "Number"))
            .ToListAsync();

        return new InvoiceView(invoice.Id, header.Number, invoice.Status.ToString(), header.CustomerCode,
            invoice.ClosingDate, invoice.PeriodFrom, invoice.PeriodTo, header.DueDate,
            invoice.PreviousBalance.Yen, invoice.ReceivedAmount.Yen, invoice.CarriedOver.Yen,
            invoice.SalesAmount.Yen, invoice.TaxAmount.Yen, invoice.TotalAmount.Yen,
            invoice.Tax.Summaries.Select(s => new InvoiceTaxSummaryView(s.Rate.Percent, s.TaxableAmount.Yen, s.Tax.Yen)).ToList(),
            recordNumbers);
    }
}
