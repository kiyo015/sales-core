namespace SalesCore.Integration.Tests;

/// <summary>決まった時刻を返す時計。日時を確かめるテストで、DbContext や API の時計と差し替える。</summary>
public sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    /// <summary>今の時刻。進めると、トークンの有効期限が切れた後を再現できる。</summary>
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();

    // サーバーのタイムゾーンは UTC にする(コンテナでは普通)。手元の機械(日本時間)でも、日本時間をサーバーの時刻に頼って出す誤りを捕まえる
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}
