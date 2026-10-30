using Microsoft.EntityFrameworkCore;
using SalesCore.Domain;
using SalesCore.Infrastructure.Persistence;

namespace SalesCore.Application;

/// <summary>
/// マスタ(得意先・商品・倉庫)の一覧と登録。参照は全員、登録は管理者だけ(業務ルール集「8.」の「役割」)。
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

    // 無効にしたマスタのコードも使えない(物理削除しないので、コードは残る)
    private static async Task EnsureNewCodeAsync(IQueryable<string> codes, string label, string? code)
    {
        if (code is not null && await codes.AnyAsync(c => c == code))
        {
            throw new BusinessRuleException($"{label} {code} は既に登録されている(無効にしたものを含む)。別のコードにする。");
        }
    }
}
