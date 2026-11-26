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
        // 消費税は締め日の月の残高に入るので、月次締めした月の締め日では締めない
        MonthlyClosing.EnsureOpen(await MonthlyClosingUseCases.LatestAsync(db), request.ClosingDate, "締め日");

        // 繰越(前回請求額・入金額)と前受の充当に使うので、請求先の請求書と入金を全部読む(追跡する。前受を充てると書き換わる)。
        // 元の入金も同じ請求先なので一緒に読まれ、打ち消しの入金から元の入金日が分かる
        // ponytail: 全部読む。請求書は請求先ごとに月1件、入金も月に数件なので、何年も先まで問題にならない。多くなったら残高の表(Day58)から始める
        var earlierInvoices = await db.Invoices.Where(i => EF.Property<long>(i, "BillingCustomerId") == customer.Id).ToListAsync();
        var receipts = await db.Receipts.Where(r => EF.Property<long>(r, "BillingCustomerId") == customer.Id).ToListAsync();
        var lastClosingDate = earlierInvoices.Max(i => (DateOnly?)i.ClosingDate);
        if (lastClosingDate >= request.ClosingDate)
        {
            throw new BusinessRuleException($"{customer.Code} は {lastClosingDate:yyyy-MM-dd} まで締め済み。{request.ClosingDate:yyyy-MM-dd} では締められない。");
        }
        var periodFrom = (lastClosingDate ?? customer.PreviousClosingDate(request.ClosingDate)).AddDays(1);

        var records = await db.SalesRecords.Include(r => r.Lines)
            .Where(r => EF.Property<long>(r, "BillingCustomerId") == customer.Id && r.InvoiceId == null && r.RecordedOn <= request.ClosingDate)
            .ToListAsync();

        var invoice = Invoice.Create(customer, periodFrom, request.ClosingDate, records, earlierInvoices, receipts);
        db.Add(invoice);
        // 前受は、締めた請求書を含む未回収の請求書に、古い入金から充てる(2026-11-20 本人の決定)。
        // 同時に入金の登録・取消があれば、入金か請求書の版数で後の方が止まる
        Receipt.AllocateAdvances(receipts, [.. earlierInvoices, invoice]);
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
            .Select(i => new { Invoice = i, Number = EF.Property<string>(i, "Number"), i.BillingCustomer.Code })
            .ToListAsync();
        // 未回収は Domain の式(今回分 − 消し込んだ額)で出す
        return invoices.Select(i => new InvoiceSummary(
                i.Invoice.Id, i.Number, i.Invoice.Status.ToString(), i.Code, i.Invoice.ClosingDate, i.Invoice.TotalAmount.Yen, i.Invoice.Outstanding.Yen))
            .ToList();
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
                select new { Number = EF.Property<string>(i, "Number"), CustomerCode = c.Code })
            .SingleAsync();
        var recordNumbers = await db.SalesRecords.Where(r => r.Invoice != null && r.Invoice.Id == id)
            .OrderBy(r => r.Id)
            .Select(r => EF.Property<string>(r, "Number"))
            .ToListAsync();

        return new InvoiceView(invoice.Id, header.Number, invoice.Status.ToString(), header.CustomerCode,
            invoice.ClosingDate, invoice.PeriodFrom, invoice.PeriodTo, invoice.DueDate,
            invoice.PreviousBalance.Yen, invoice.ReceivedAmount.Yen, invoice.CarriedOver.Yen,
            invoice.SalesAmount.Yen, invoice.TaxAmount.Yen, invoice.TotalAmount.Yen,
            invoice.Tax.Summaries.Select(s => new InvoiceTaxSummaryView(s.Rate.Percent, s.TaxableAmount.Yen, s.Tax.Yen)).ToList(),
            recordNumbers, invoice.AllocatedAmount.Yen, invoice.Outstanding.Yen);
    }
}
