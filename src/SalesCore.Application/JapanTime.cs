namespace SalesCore.Application;

/// <summary>日付は日本時間で決める(日本は夏時間が無いので、+9時間で固定)。サーバーのタイムゾーンに頼らない(コンテナでは UTC が普通)。</summary>
public static class JapanTime
{
    public static DateOnly Today(this TimeProvider clock) =>
        DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(9)).DateTime);
}
