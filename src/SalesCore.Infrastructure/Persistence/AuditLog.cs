namespace SalesCore.Infrastructure.Persistence;

/// <summary>
/// 監査ログ。金額・数量・状態を変えた操作を、誰がいつ行ったかとあわせて残す(業務ルール集「7. データの扱い」)。
/// 表だけを Day25 に作り、書き込む仕組みは Day27 で作る。操作した人(user_id)は、ユーザーを作る段階2までは空。
/// </summary>
public sealed class AuditLog
{
    public long Id { get; set; }

    public string? UserId { get; set; }

    /// <summary>対象の表(sales_orders など)。</summary>
    public string EntityType { get; set; } = "";

    public long EntityId { get; set; }

    /// <summary>作成・更新・状態変更・取消。</summary>
    public string Action { get; set; } = "";

    /// <summary>変更前の値(JSON)。</summary>
    public string? BeforeValues { get; set; }

    /// <summary>変更後の値(JSON)。</summary>
    public string? AfterValues { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}
