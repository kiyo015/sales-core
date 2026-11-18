using Microsoft.EntityFrameworkCore;
using SalesCore.Domain;
using SalesCore.Infrastructure.Persistence;

namespace SalesCore.Application;

/// <summary>
/// 見積のユースケース(業務ルール集「3. 販売の流れ」の「見積と得意先別単価」)。状態と金額の判断は Domain(<see cref="Quotation"/>)が行う。
/// 得意先・商品・単価・税率の引き方は受注の登録と同じ(<see cref="SalesOrderUseCases"/> と共有)で、日付が見積日になる。
/// 「今日」(提出と受注化の有効期限、受注日)は、サーバーの時計の日本時間で決める。
/// </summary>
public sealed class QuotationUseCases(IDbContextFactory<SalesCoreDbContext> sessions, ICurrentUser user, TimeProvider clock, ApprovalThreshold threshold)
{
    public async Task<QuotationView> CreateAsync(CreateQuotation request)
    {
        Permissions.Require(user, Operation.ManageQuotation);
        var validUntil = request.ValidUntil ?? throw new InvalidInputException("有効期限を入れる。"); // 既定値は持たない(2026-11-18 本人の決定)
        await using var db = await sessions.CreateDbContextAsync();
        var customer = await SalesOrderUseCases.ActiveCustomerAsync(db, request.CustomerCode);
        var prices = await SalesOrderUseCases.CustomerPricesAsync(db, customer);

        var lines = new List<QuotationLine>();
        foreach (var requested in request.Lines ?? [])
        {
            _ = requested ?? throw new InvalidInputException("明細が空(null)。");
            var product = await SalesOrderUseCases.ActiveProductAsync(db, requested.ProductCode);
            var rate = await SalesOrderUseCases.TaxRateOnAsync(db, product, request.QuotedOn);
            var unitPrice = CustomerPrice.UnitPriceFor(customer, product, request.QuotedOn, prices, requested.UnitPrice);
            lines.Add(new QuotationLine(product, requested.Quantity, unitPrice, rate));
        }

        var quotation = new Quotation(customer, request.QuotedOn, validUntil, lines);
        db.Add(quotation);
        await db.SaveChangesAsync();
        return await ReadAsync(db, quotation.Id);
    }

    public async Task<QuotationView> GetAsync(long id)
    {
        Permissions.Require(user, Operation.ViewQuotation);
        await using var db = await sessions.CreateDbContextAsync();
        return await ReadAsync(db, id);
    }

    public Task<QuotationView> ChangeLineAsync(long id, long lineId, ChangeQuotationLine request) => ChangeAsync(id, quotation =>
        quotation.ChangeLine(
            quotation.Lines.SingleOrDefault(l => l.Id == lineId) ?? throw new InvalidInputException($"見積明細 {lineId} はこの見積の明細ではない。"),
            request.Quantity, request.UnitPrice));

    public Task<QuotationView> SubmitAsync(long id) => ChangeAsync(id, quotation => quotation.Submit(clock.Today()));

    public Task<QuotationView> MarkLostAsync(long id) => ChangeAsync(id, quotation => quotation.MarkLost());

    /// <summary>
    /// 見積の全明細を1つの受注にする。受注日は日本時間の今日、税率は受注日の税率(単価は見積のまま)。
    /// 受注の登録と同じく、無効になった得意先・商品と、受注日の税率が無い商品では作らない。元の見積は sales_orders.quotation_id に残す。
    /// </summary>
    public async Task<SalesOrderView> OrderAsync(long id)
    {
        Permissions.Require(user, Operation.ManageQuotation);
        await using var db = await sessions.CreateDbContextAsync();
        var quotation = await LoadAsync(db, id);
        var today = clock.Today();
        if (!quotation.Customer.IsActive) throw new InvalidInputException($"得意先コード {quotation.Customer.Code} の得意先が無い(無効を含む)。");
        var rates = new Dictionary<Product, TaxRate>();
        foreach (var product in quotation.Lines.Select(l => l.Product).Distinct())
        {
            if (!product.IsActive) throw new InvalidInputException($"商品コード {product.Code} の商品が無い(無効を含む)。");
            rates[product] = await SalesOrderUseCases.TaxRateOnAsync(db, product, today);
        }

        var order = quotation.ToSalesOrder(today, product => rates[product], threshold.Amount);
        // 受注明細の商品は、見積明細の同じ位置から写す(ToSalesOrder の約束)。件数が違えば Zip が黙って短い方に合わせるので止める(PR #26 の AIレビュー)
        if (order.Lines.Count != quotation.Lines.Count)
        {
            throw new InvalidOperationException($"見積 {id} の明細 {quotation.Lines.Count} 行に対し、受注明細が {order.Lines.Count} 行できた。");
        }
        db.Add(order);
        db.Entry(order).Property("QuotationId").CurrentValue = quotation.Id;
        foreach (var (orderLine, quotationLine) in order.Lines.Zip(quotation.Lines))
        {
            db.Entry(orderLine).Property("ProductId").CurrentValue = quotationLine.Product.Id;
        }
        await db.SaveChangesAsync();
        return await SalesOrderUseCases.ReadOrderAsync(db, order.Id);
    }

    private async Task<QuotationView> ChangeAsync(long id, Action<Quotation> change)
    {
        Permissions.Require(user, Operation.ManageQuotation);
        await using var db = await sessions.CreateDbContextAsync();
        change(await LoadAsync(db, id));
        await db.SaveChangesAsync();
        return await ReadAsync(db, id);
    }

    private static async Task<Quotation> LoadAsync(SalesCoreDbContext db, long id) =>
        await db.Quotations.Include(q => q.Customer).Include(q => q.Lines).ThenInclude(l => l.Product).ThenInclude(p => p.TaxCategory)
            .SingleOrDefaultAsync(q => q.Id == id)
        ?? throw new NotFoundException($"見積 {id} が無い。");

    private static async Task<QuotationView> ReadAsync(SalesCoreDbContext db, long id)
    {
        var quotation = await db.Quotations.AsNoTracking().Include(q => q.Customer).Include(q => q.Lines).ThenInclude(l => l.Product)
                .SingleOrDefaultAsync(q => q.Id == id)
            ?? throw new NotFoundException($"見積 {id} が無い。");
        var number = await db.Quotations.Where(q => q.Id == id).Select(q => EF.Property<string>(q, "Number")).SingleAsync();
        return new QuotationView(quotation.Id, number, quotation.Status.ToString(), quotation.Customer.Code, quotation.QuotedOn, quotation.ValidUntil,
            quotation.Amount.Yen, quotation.Tax.TaxTotal.Yen,
            quotation.Lines.OrderBy(l => l.Id).Select(l => new QuotationLineView(l.Id, l.Product.Code, l.Quantity, l.UnitPrice, l.TaxRate.Percent)).ToList());
    }
}
