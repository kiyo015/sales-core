using Microsoft.EntityFrameworkCore;
using Npgsql;
using SalesCore.Domain;

namespace SalesCore.Application;

/// <summary>
/// 操作の誤りを、HTTP の状態コードと利用者に見せる理由にする。API と画面の両方がこれを使い、同じ扱いにそろえる。
/// 業務の誤り(Domain と Application の例外型)だけ理由を見せる。標準の ArgumentException・InvalidOperationException は
/// EF Core や LINQ も投げ、内部のメッセージが見えるので扱わない(<see cref="From"/> が null。API は 500、画面は決まった文言)。
/// </summary>
public sealed record OperationError(int Status, string Message)
{
    /// <summary>一意インデックス → 何が重なったか。インデックスを足したら、ここにも足す(OperationErrorTests が止める)。</summary>
    public static IReadOnlyDictionary<string, string> UniqueConstraints { get; } = new Dictionary<string, string>
    {
        ["IX_customers_code"] = "得意先コード",
        ["IX_products_code"] = "商品コード",
        ["IX_warehouses_code"] = "倉庫コード",
        ["IX_tax_categories_code"] = "税区分コード",
        ["IX_tax_rates_tax_category_id_valid_from"] = "同じ税区分・同じ開始日の税率",
        ["IX_invoices_billing_customer_id_closing_date"] = "同じ請求先・同じ締め日の請求書",
        ["IX_shipment_lines_shipment_id_sales_order_line_id"] = "1回の出荷の中の同じ受注明細",
        ["IX_sales_orders_number"] = "受注番号",
        ["IX_shipments_number"] = "出荷番号",
        ["IX_sales_records_number"] = "売上番号",
        ["IX_invoices_number"] = "請求書番号",
        ["IX_quotations_number"] = "見積番号",
        ["IX_sales_orders_quotation_id"] = "同じ見積からの受注",
        ["IX_receipts_number"] = "入金番号",
        ["IX_receipts_reverses_id"] = "同じ入金の取消", // 同時に2人が取り消した時(2026-11-20)
    };

    public static OperationError? From(Exception exception) => exception switch
    {
        UnauthenticatedException e => new(401, e.Message), // ログインしていない・トークンの期限が切れた
        ForbiddenException e => new(403, e.Message), // 役割が足りない
        NotFoundException e => new(404, e.Message),
        InvalidInputException e => new(400, e.Message),
        BusinessRuleException e => new(409, e.Message), // 状態に合わない操作・コードの重複
        DbUpdateConcurrencyException => new(409, "ほかの人が先に変更した。読み直してからやり直す。"),
        // 確かめた後、保存までの間に、ほかの人が同じものを先に登録した(同じコードの得意先・同じ締め日の請求書など)
        DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } unique } =>
            new(409, $"{UniqueConstraints.GetValueOrDefault(unique.ConstraintName ?? "", "同じもの")}は、ほかの人が先に登録した。読み直してからやり直す。"),
        // 確かめた後、保存までの間に、ほかの人が期間の重なる得意先別単価を先に登録した(排他制約 ex_customer_prices_no_overlap)
        // 制約の名前で分ける(税率の ex_tax_rates_no_overlap に誤った理由を返さない。PR #24 の AIレビュー)
        DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.ExclusionViolation, ConstraintName: "ex_customer_prices_no_overlap" } } =>
            new(409, "ほかの人が先に登録した単価と期間が重なる。読み直してからやり直す。"),
        // DB の桁あふれ(22003)は扱わない。入力の桁は Domain が理由つきで止める(SalesOrderLine.EnsureLine・MasterInput.UnitPrice)ので、
        // ここに来るのは入力でない値(集計など)。利用者の入力の誤りにしない(Day42 からの持ち越し。2026-11-17)
        _ => null,
    };
}
