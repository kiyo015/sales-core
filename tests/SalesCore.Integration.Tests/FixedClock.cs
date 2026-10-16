namespace SalesCore.Integration.Tests;

/// <summary>決まった時刻を返す時計。日時を確かめるテストで、DbContext や API の時計と差し替える。</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
}
