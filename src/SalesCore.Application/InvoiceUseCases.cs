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
    private DateOnly Today => clock.Today(); // 日本時間の今日

    /// <summary>
    /// 請求先の締め日で締め、請求書を作る。請求先あての、締め日までの未請求の売上を全部入れる(2026-10-16 決定)。
    /// 期間は前回の締め日の翌日から今回の締め日まで(最初の締めは、前の月の締め日の翌日から)。
    /// </summary>
    public async Task<InvoiceView> CloseAsync(CloseInvoice request)
    {
        Permissions.Require(user, Operation.CloseInvoice);
        // 画面で選ばずに送った時(空)は、「コード  の…が無い」でなく、選ぶよう求める
        if (string.IsNullOrWhiteSpace(request.BillingCustomerCode)) throw new InvalidInputException("請求先を選ぶ。");
        await using var db = await sessions.CreateDbContextAsync();
        // 無効にした得意先でも、残っている売上は請求するので、無効かどうかは見ない。請求先で締めているかは Domain が確かめる
        var customer = await db.Customers.Include(c => c.BillingCustomer).SingleOrDefaultAsync(c => c.Code == request.BillingCustomerCode)
            ?? throw new InvalidInputException($"得意先コード {request.BillingCustomerCode} の得意先が無い。");
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

        var invoice = Invoice.Create(customer, periodFrom, request.ClosingDate, records);
        db.Add(invoice);
        await db.SaveChangesAsync();
        return await ReadAsync(db, invoice.Id);
    }

    /// <summary>締めの画面の請求先の選択肢。無効にした得意先も、残っている売上を請求するので出す。</summary>
    public async Task<IReadOnlyList<BillingCustomerView>> ListBillingCustomersAsync()
    {
        Permissions.Require(user, Operation.CloseInvoice);
        await using var db = await sessions.CreateDbContextAsync();
        var customers = await db.Customers.AsNoTracking().Where(c => c.BillingCustomerId == null).OrderBy(c => c.Code).ToListAsync();
        return customers.Select(c => new BillingCustomerView(c.Code, c.Name, c.ClosingDay, c.IsActive, c.LatestClosingDate(Today))).ToList();
    }

    /// <summary>請求書の一覧。新しい順(締めた順)。</summary>
    // ponytail: 全件を読む(受注の一覧と同じ)。件数が増えたら、締め日や請求先で絞る・ページに分ける
    public async Task<IReadOnlyList<InvoiceSummary>> ListAsync()
    {
        Permissions.Require(user, Operation.ViewInvoice);
        await using var db = await sessions.CreateDbContextAsync();
        var invoices = await db.Invoices.AsNoTracking().OrderByDescending(i => i.Id)
            .Select(i => new { i.Id, Number = EF.Property<string>(i, "Number"), i.Status, i.BillingCustomer.Code, i.ClosingDate, i.TotalAmount })
            .ToListAsync();
        return invoices.Select(i => new InvoiceSummary(i.Id, i.Number, i.Status.ToString(), i.Code, i.ClosingDate, i.TotalAmount.Yen)).ToList();
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
