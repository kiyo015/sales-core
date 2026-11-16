using Microsoft.EntityFrameworkCore;
using SalesCore.Domain;
using SalesCore.Infrastructure.Persistence;

namespace SalesCore.Application;

/// <summary>
/// マスタ(得意先・商品・倉庫・得意先別単価)の一覧と登録。参照は全員、登録は管理者だけ(業務ルール集「8.」の「役割」)。
/// 入力の決まり(空・長さ・締め日・単価・請求先は1段まで)は Domain が確かめる。ここでは、コードの重複と、指したコードがあるかを見る。
/// 確かめた後に、ほかの人が同じコードを先に登録すると、保存の時に一意インデックスに当たる(<see cref="OperationError"/> が 409 にする)。
/// </summary>
public sealed class MasterUseCases(IDbContextFactory<SalesCoreDbContext> sessions, ICurrentUser user)
{
    public async Task<IReadOnlyList<CustomerView>> ListCustomersAsync()
    {
        Permissions.Require(user, Operation.ViewMasters);
        await using var db = await sessions.CreateDbContextAsync();
        return await db.Customers.OrderBy(c => c.Code)
            .Select(c => new CustomerView(c.Code, c.Name, c.ClosingDay, c.BillingCustomer == null ? null : c.BillingCustomer.Code, c.IsActive))
            .ToListAsync();
    }

    public async Task<IReadOnlyList<ProductView>> ListProductsAsync()
    {
        Permissions.Require(user, Operation.ViewMasters);
        await using var db = await sessions.CreateDbContextAsync();
        return await db.Products.OrderBy(p => p.Code)
            .Select(p => new ProductView(p.Code, p.Name, p.Unit, p.TaxCategory.Code, p.StandardPrice, p.IsActive))
            .ToListAsync();
    }

    public async Task<IReadOnlyList<WarehouseView>> ListWarehousesAsync()
    {
        Permissions.Require(user, Operation.ViewMasters);
        await using var db = await sessions.CreateDbContextAsync();
        return await db.Warehouses.OrderBy(w => w.Code).Select(w => new WarehouseView(w.Code, w.Name, w.IsActive)).ToListAsync();
    }

    /// <summary>商品の登録で選ぶ税区分。</summary>
    public async Task<IReadOnlyList<TaxCategoryView>> ListTaxCategoriesAsync()
    {
        Permissions.Require(user, Operation.ViewMasters);
        await using var db = await sessions.CreateDbContextAsync();
        return await db.TaxCategories.OrderBy(t => t.Code).Select(t => new TaxCategoryView(t.Code, t.Name)).ToListAsync();
    }

    public async Task<CustomerView> RegisterCustomerAsync(RegisterCustomer request)
    {
        Permissions.Require(user, Operation.RegisterMaster);
        await using var db = await sessions.CreateDbContextAsync();
        await EnsureNewCodeAsync(db.Customers.Select(c => c.Code), "得意先コード", request.Code);
        var billing = string.IsNullOrEmpty(request.BillingCustomerCode) ? null
            : await db.Customers.Include(c => c.BillingCustomer).SingleOrDefaultAsync(c => c.Code == request.BillingCustomerCode)
                ?? throw new InvalidInputException($"請求先の得意先コード {request.BillingCustomerCode} の得意先が無い。");

        var customer = new Customer(request.Code ?? "", request.Name ?? "", request.ClosingDay, billing);
        db.Add(customer);
        await db.SaveChangesAsync();
        return new CustomerView(customer.Code, customer.Name, customer.ClosingDay, billing?.Code, customer.IsActive);
    }

    public async Task<ProductView> RegisterProductAsync(RegisterProduct request)
    {
        Permissions.Require(user, Operation.RegisterMaster);
        await using var db = await sessions.CreateDbContextAsync();
        await EnsureNewCodeAsync(db.Products.Select(p => p.Code), "商品コード", request.Code);
        // 選ばずに送った時(空)は、「税区分コード  の税区分が無い」でなく、選ぶよう求める(PR #14 の AIレビュー3回目の Minor)
        if (string.IsNullOrWhiteSpace(request.TaxCategoryCode)) throw new InvalidInputException("税区分を選ぶ。");
        var taxCategory = await db.TaxCategories.SingleOrDefaultAsync(t => t.Code == request.TaxCategoryCode)
            ?? throw new InvalidInputException($"税区分コード {request.TaxCategoryCode} の税区分が無い。");

        var product = new Product(request.Code ?? "", request.Name ?? "", request.Unit ?? "", taxCategory, request.StandardPrice);
        db.Add(product);
        await db.SaveChangesAsync();
        return new ProductView(product.Code, product.Name, product.Unit, taxCategory.Code, product.StandardPrice, product.IsActive);
    }

    public async Task<WarehouseView> RegisterWarehouseAsync(RegisterWarehouse request)
    {
        Permissions.Require(user, Operation.RegisterMaster);
        await using var db = await sessions.CreateDbContextAsync();
        await EnsureNewCodeAsync(db.Warehouses.Select(w => w.Code), "倉庫コード", request.Code);

        var warehouse = new Warehouse(request.Code ?? "", request.Name ?? "");
        db.Add(warehouse);
        await db.SaveChangesAsync();
        return new WarehouseView(warehouse.Code, warehouse.Name, warehouse.IsActive);
    }

    /// <summary>得意先別単価の一覧。得意先・商品・初日の順(無効を含む)。</summary>
    public async Task<IReadOnlyList<CustomerPriceView>> ListCustomerPricesAsync()
    {
        Permissions.Require(user, Operation.ViewMasters);
        await using var db = await sessions.CreateDbContextAsync();
        return await db.CustomerPrices.OrderBy(p => p.Customer.Code).ThenBy(p => p.Product.Code).ThenBy(p => p.ValidFrom).ThenBy(p => p.Id)
            .Select(p => new CustomerPriceView(p.Id, p.Customer.Code, p.Product.Code, p.UnitPrice, p.ValidFrom, p.ValidTo, p.IsActive))
            .ToListAsync();
    }

    /// <summary>
    /// 得意先別単価を登録する。無期限の前の単価は、新しい初日の前日で閉じ、新しい単価が末日つきなら翌日から続ける(Domain が決める)。
    /// 同時に2人が登録した時は、DB の排他制約か前の単価の版数で、後の方が 409 になる。
    /// </summary>
    public async Task<CustomerPriceView> RegisterCustomerPriceAsync(RegisterCustomerPrice request)
    {
        Permissions.Require(user, Operation.RegisterCustomerPrice);
        if (string.IsNullOrWhiteSpace(request.CustomerCode)) throw new InvalidInputException("得意先を選ぶ。");
        if (string.IsNullOrWhiteSpace(request.ProductCode)) throw new InvalidInputException("商品を選ぶ。");
        await using var db = await sessions.CreateDbContextAsync();
        var customer = await db.Customers.Include(c => c.BillingCustomer).SingleOrDefaultAsync(c => c.Code == request.CustomerCode && c.IsActive)
            ?? throw new InvalidInputException($"得意先コード {request.CustomerCode} の得意先が無い(無効を含む)。");
        var product = await db.Products.SingleOrDefaultAsync(p => p.Code == request.ProductCode && p.IsActive)
            ?? throw new InvalidInputException($"商品コード {request.ProductCode} の商品が無い(無効を含む)。");
        var existing = await db.CustomerPrices.Include(p => p.Customer).Include(p => p.Product)
            .Where(p => p.Customer.Id == customer.Id && p.Product.Id == product.Id && p.IsActive).ToListAsync();

        var (price, continued) = CustomerPrice.Register(customer, product, request.UnitPrice, request.ValidFrom, request.ValidTo, existing);
        db.Add(price);
        if (continued is not null) db.Add(continued);
        await db.SaveChangesAsync();
        return new CustomerPriceView(price.Id, customer.Code, product.Code, price.UnitPrice, price.ValidFrom, price.ValidTo, price.IsActive);
    }

    /// <summary>誤って登録した得意先別単価を無効にする。受注明細に写した単価は変わらない。</summary>
    public async Task<CustomerPriceView> InvalidateCustomerPriceAsync(long id)
    {
        Permissions.Require(user, Operation.InvalidateCustomerPrice);
        await using var db = await sessions.CreateDbContextAsync();
        var price = await db.CustomerPrices.Include(p => p.Customer).Include(p => p.Product).SingleOrDefaultAsync(p => p.Id == id)
            ?? throw new NotFoundException($"得意先別単価 {id} が無い。");
        price.Invalidate();
        await db.SaveChangesAsync();
        return new CustomerPriceView(price.Id, price.Customer.Code, price.Product.Code, price.UnitPrice, price.ValidFrom, price.ValidTo, price.IsActive);
    }

    // 無効にしたマスタのコードも使えない(物理削除しないので、コードは残る)
    private static async Task EnsureNewCodeAsync(IQueryable<string> codes, string label, string? code)
    {
        if (code is not null && await codes.AnyAsync(c => c == code))
        {
            throw new BusinessRuleException($"{label} {code} は既に登録されている(無効にしたものを含む)。別のコードにする。");
        }
    }
}
