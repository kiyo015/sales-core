using Microsoft.EntityFrameworkCore;
using SalesCore.Domain;
using SalesCore.Infrastructure.Persistence;

namespace SalesCore.Application;

/// <summary>
/// 入金と消込(業務ルール集「4. 請求」の「入金と消込」)。どの請求書にいくら充てるか・取消の打ち消しは Domain(<see cref="Receipt"/>)が決め、
/// ここでは請求先と、その請求先の請求書を集めて渡す。「今日」(入金日の上限、取消の日付)は、サーバーの時計の日本時間で決める。
/// </summary>
public sealed class ReceiptUseCases(IDbContextFactory<SalesCoreDbContext> sessions, ICurrentUser user, TimeProvider clock)
{
    public async Task<ReceiptView> RegisterAsync(RegisterReceipt request)
    {
        Permissions.Require(user, Operation.RegisterReceipt);
        if (string.IsNullOrWhiteSpace(request.BillingCustomerCode)) throw new InvalidInputException("請求先を選ぶ。");
        var receivedOn = request.ReceivedOn ?? throw new InvalidInputException("入金日を入れる。");
        var amount = request.Amount ?? throw new InvalidInputException("入金額を入れる。");
        if (amount % 1m != 0m) throw new InvalidInputException($"入金額 {amount} 円は、円単位で入れる。"); // Money は端数を丸めずに例外(500)にする
        await using var db = await sessions.CreateDbContextAsync();
        // 無効にした請求先からも受ける(2026-11-24 本人の決定。締め請求と同じ)。支店でないことは Domain が確かめる
        var customer = await db.Customers.Include(c => c.BillingCustomer).SingleOrDefaultAsync(c => c.Code == request.BillingCustomerCode)
            ?? throw new InvalidInputException($"得意先コード {request.BillingCustomerCode} の得意先が無い。");
        // ponytail: 請求先の請求書を全部読み、未回収かどうかは Domain が見る(金額は変換子で対応付けているので SQL で比べられない)。
        // 請求書は請求先ごとに月1件なので、件数が問題になるのは何年も先。その時は未回収の列を持つか SQL で絞る
        var invoices = await db.Invoices.Where(i => EF.Property<long>(i, "BillingCustomerId") == customer.Id).ToListAsync();

        var receipt = Receipt.Register(customer, receivedOn, Money.Of(amount), invoices, clock.Today());
        db.Add(receipt);
        await db.SaveChangesAsync();
        return await ReadAsync(db, receipt.Id);
    }

    /// <summary>入金を取り消す(赤黒)。取り消した日(日本時間の今日)の日付で、打ち消しの入金を立てて返す。</summary>
    public async Task<ReceiptView> CancelAsync(long id)
    {
        Permissions.Require(user, Operation.CancelReceipt);
        await using var db = await sessions.CreateDbContextAsync();
        var receipt = await db.Receipts.Include(r => r.BillingCustomer).Include(r => r.Allocations).ThenInclude(a => a.Invoice)
            .SingleOrDefaultAsync(r => r.Id == id)
            ?? throw new NotFoundException($"入金 {id} が無い。");

        var reversal = receipt.Cancel(clock.Today());
        db.Add(reversal);
        await db.SaveChangesAsync();
        return await ReadAsync(db, reversal.Id);
    }

    /// <summary>入金の一覧。新しい順(登録の順)。取り消した入金と打ち消しの入金を両方並べる(2026-11-24 本人の決定)。</summary>
    // ponytail: 全件を読む(受注・請求書の一覧と同じ)。件数が増えたら、期間や請求先で絞る・ページに分ける
    public async Task<IReadOnlyList<ReceiptSummary>> ListAsync()
    {
        Permissions.Require(user, Operation.ViewReceipt);
        await using var db = await sessions.CreateDbContextAsync();
        var rows = await db.Receipts.AsNoTracking().OrderByDescending(r => r.Id)
            .Select(r => new
            {
                Receipt = r,
                Number = EF.Property<string>(r, "Number"),
                r.BillingCustomer.Code,
                ReversesNumber = r.Reverses == null ? null : EF.Property<string>(r.Reverses, "Number"),
            })
            .ToListAsync();
        return rows.Select(r => new ReceiptSummary(
                r.Receipt.Id, r.Number, r.Code, r.Receipt.ReceivedOn, r.Receipt.Amount.Yen, r.Receipt.Unallocated.Yen, r.Receipt.IsCancelled, r.ReversesNumber))
            .ToList();
    }

    public async Task<ReceiptView> GetAsync(long id)
    {
        Permissions.Require(user, Operation.ViewReceipt);
        await using var db = await sessions.CreateDbContextAsync();
        return await ReadAsync(db, id);
    }

    /// <summary>登録の画面の請求先の選択肢。無効にした請求先も、残った売掛金を回収するので出す(2026-11-24 本人の決定)。</summary>
    public async Task<IReadOnlyList<BillingCustomerView>> ListBillingCustomersAsync()
    {
        Permissions.Require(user, Operation.RegisterReceipt);
        await using var db = await sessions.CreateDbContextAsync();
        var today = clock.Today();
        var customers = await db.Customers.AsNoTracking().Where(c => c.BillingCustomerId == null).OrderBy(c => c.Code).ToListAsync();
        return customers.Select(c => new BillingCustomerView(c.Code, c.Name, c.ClosingDay, c.IsActive, c.LatestClosingDate(today))).ToList();
    }

    /// <summary>
    /// 請求先の未回収の請求書を、入金を充てる順(締め日の古い順、同じ日なら Id の順。<see cref="Receipt.Register"/> と同じ)に返す。
    /// 登録の画面で、入金額を入れる前にどこに充たるかを確かめるため(2026-11-24 本人の決定)。無い請求先なら空。
    /// </summary>
    public async Task<IReadOnlyList<OutstandingInvoiceView>> ListOutstandingInvoicesAsync(string billingCustomerCode)
    {
        Permissions.Require(user, Operation.RegisterReceipt);
        await using var db = await sessions.CreateDbContextAsync();
        var rows = await db.Invoices.AsNoTracking().Where(i => i.BillingCustomer.Code == billingCustomerCode)
            .Select(i => new { Invoice = i, Number = EF.Property<string>(i, "Number") })
            .ToListAsync();
        return rows.Where(r => r.Invoice.Outstanding.Yen > 0m)
            .OrderBy(r => r.Invoice.ClosingDate).ThenBy(r => r.Invoice.Id)
            .Select(r => new OutstandingInvoiceView(r.Invoice.Id, r.Number, r.Invoice.ClosingDate, r.Invoice.Outstanding.Yen))
            .ToList();
    }

    // 登録・取消の後の読み直しにも使うので、参照の検査はしない(検査は呼ぶ側の入口で済んでいる)
    private static async Task<ReceiptView> ReadAsync(SalesCoreDbContext db, long id)
    {
        var row = await db.Receipts.AsNoTracking().Where(r => r.Id == id)
            .Select(r => new
            {
                Receipt = r,
                Number = EF.Property<string>(r, "Number"),
                r.BillingCustomer.Code,
                Reverses = r.Reverses == null ? null : new ReceiptLink(r.Reverses.Id, EF.Property<string>(r.Reverses, "Number")),
            })
            .SingleOrDefaultAsync()
            ?? throw new NotFoundException($"入金 {id} が無い。");
        var reversedBy = await db.Receipts.Where(r => r.ReversesId == id)
            .Select(r => new ReceiptLink(r.Id, EF.Property<string>(r, "Number")))
            .SingleOrDefaultAsync();
        var allocations = await db.Receipts.Where(r => r.Id == id).SelectMany(r => r.Allocations).OrderBy(a => a.Id)
            .Select(a => new { a.Invoice.Id, Number = EF.Property<string>(a.Invoice, "Number"), a.Invoice.ClosingDate, a.Amount })
            .ToListAsync();

        var receipt = row.Receipt;
        return new ReceiptView(receipt.Id, row.Number, row.Code, receipt.ReceivedOn, receipt.Amount.Yen, receipt.AllocatedAmount.Yen, receipt.Unallocated.Yen,
            receipt.IsCancelled, row.Reverses, reversedBy,
            allocations.Select(a => new ReceiptAllocationView(a.Id, a.Number, a.ClosingDate, a.Amount.Yen)).ToList());
    }
}
