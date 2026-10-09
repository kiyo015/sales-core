using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SalesCore.Domain;
using SalesCore.Infrastructure.Persistence;

namespace SalesCore.Integration.Tests;

/// <summary>
/// 監査ログ・作成者と更新日時・同時更新の衝突・物理削除の禁止(業務ルール集「7. データの扱い」)。
/// - すべての表に、作成者・作成日時・更新者・更新日時・版数を持つ
/// - 金額・数量・状態を変えた操作は、変更前と変更後の値を監査ログに残す
/// - 同じデータを2人が同時に変えたら、後から保存した方を止める(楽観ロック)
/// - 物理削除しない
/// </summary>
public class AuditAndConcurrencyTests(DatabaseFixture fixture) : DatabaseTest(fixture)
{
    private static readonly TaxRate Standard = TaxRate.Of(10m);
    private static readonly DateTimeOffset Morning = new(2026, 10, 9, 9, 0, 0, TimeSpan.FromHours(9));
    private static readonly DateTimeOffset Noon = new(2026, 10, 9, 12, 0, 0, TimeSpan.FromHours(9));

    // ── 作成者・更新日時 ──
    [Fact]
    public async Task 作った人と日時_最後に変えた人と日時が残る()
    {
        var scenario = new TestScenario(Db);
        var m = await scenario.SeedMastersAsync();
        Db.Clock = new FixedClock(Morning);
        Db.CurrentUser = "sato";
        var order = scenario.AddOrder(m.HeadOffice, new DateOnly(2026, 10, 9), (m.StandardProduct, 1, Standard));
        await scenario.EndRequestAsync();

        Db.Clock = new FixedClock(Noon);
        Db.CurrentUser = "suzuki";
        (await Db.SalesOrders.SingleAsync(o => o.Id == order.Id)).Approve();
        await scenario.EndRequestAsync();

        var saved = await Db.SalesOrders.SingleAsync(o => o.Id == order.Id);
        var entry = Db.Entry(saved);
        Assert.Equal(("sato", Morning), (entry.Property<string?>("CreatedBy").CurrentValue, entry.Property<DateTimeOffset>("CreatedAt").CurrentValue));
        Assert.Equal(("suzuki", Noon), (entry.Property<string?>("UpdatedBy").CurrentValue, entry.Property<DateTimeOffset>("UpdatedAt").CurrentValue));
        Assert.Equal(1, entry.Property<int>("RowVersion").CurrentValue); // 作成で 0、1回変えて 1
    }

    // ── 監査ログ ──
    [Fact]
    public async Task 状態を変えると_変更前と変更後の値が監査ログに残る()
    {
        var scenario = new TestScenario(Db);
        var m = await scenario.SeedMastersAsync();
        var order = scenario.AddOrder(m.HeadOffice, new DateOnly(2026, 10, 9), (m.StandardProduct, 2, Standard));
        await scenario.EndRequestAsync();

        Db.Clock = new FixedClock(Noon);
        Db.CurrentUser = "suzuki";
        (await Db.SalesOrders.SingleAsync(o => o.Id == order.Id)).Approve();
        await scenario.EndRequestAsync();

        var logs = await LogsFor("sales_orders", order.Id);
        Assert.Equal(new[] { "created", "updated" }, logs.Select(l => l.Action).ToArray());
        var approved = logs[1];
        Assert.Equal(("suzuki", Noon), (approved.UserId, approved.OccurredAt));
        Assert.Equal("Received", Json(approved.BeforeValues)["status"].GetString());
        Assert.Equal("Approved", Json(approved.AfterValues)["status"].GetString());
        Assert.False(Json(approved.AfterValues).ContainsKey("updated_at")); // 変えた業務の値だけを残す
    }

    [Fact]
    public async Task 出荷を確定すると_受注明細の出荷済み数量の変化と_売上の金額が監査ログに残る()
    {
        var scenario = new TestScenario(Db);
        var m = await scenario.SeedMastersAsync();
        var order = scenario.AddOrder(m.HeadOffice, new DateOnly(2026, 10, 9), (m.ReducedProduct, 3, TaxRate.Of(8m)));
        order.Approve();
        var shipment = scenario.AddShipment(order.InstructShipment([new(order.Lines[0], 2)]), m.Warehouse);
        await Db.SaveChangesAsync();
        var record = scenario.AddSalesRecord(shipment.Confirm(new DateOnly(2026, 10, 9)), m.HeadOffice);
        await scenario.EndRequestAsync();

        var lineLog = (await LogsFor("sales_order_lines", order.Lines[0].Id)).Last();
        Assert.Equal(0m, Json(lineLog.BeforeValues)["shipped_quantity"].GetDecimal());
        Assert.Equal(2m, Json(lineLog.AfterValues)["shipped_quantity"].GetDecimal());

        var recordLineLog = Assert.Single(await LogsFor("sales_record_lines", record.Lines[0].Id));
        Assert.Equal("created", recordLineLog.Action);
        Assert.Equal(67m, Json(recordLineLog.AfterValues)["amount"].GetDecimal()); // 33.33 × 2 = 66.66 → 67円
    }

    // ── 同時更新の衝突 ──
    // 2人が同じ受注を開く。1人が承認して保存した後、もう1人が古い画面のまま取り消そうとしても、保存の時点で止まる
    [Fact]
    public async Task 同じ受注を2人が同時に変えると_後から保存した方が衝突で止まる()
    {
        var scenario = new TestScenario(Db);
        var m = await scenario.SeedMastersAsync();
        var order = scenario.AddOrder(m.HeadOffice, new DateOnly(2026, 10, 9), (m.StandardProduct, 1, Standard));
        await scenario.EndRequestAsync();
        await using var other = CreateAnotherSession();

        var mine = await Db.SalesOrders.SingleAsync(o => o.Id == order.Id);
        var theirs = await other.SalesOrders.SingleAsync(o => o.Id == order.Id);
        mine.Approve();
        await Db.SaveChangesAsync();
        theirs.Cancel(); // 相手の画面では、まだ受付のまま

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => other.SaveChangesAsync());
        Db.ChangeTracker.Clear();
        Assert.Equal(SalesOrderStatus.Approved, (await Db.SalesOrders.SingleAsync(o => o.Id == order.Id)).Status);
    }

    // 3行の受注で1行目は出荷済。残りの2行を、2人が別々の出荷で同時に確定する。
    // どちらも「もう1行が残っている」と見て一部出荷のままにするので、両方が通ると、全行出荷したのに状態が一部出荷のまま残る。
    // 明細が変わったら受注(集約の根)の版数も上げるので、後から保存した方が衝突で止まる
    [Fact]
    public async Task 同じ受注の別々の明細を2人が同時に出荷すると_後から保存した方が衝突で止まる()
    {
        var scenario = new TestScenario(Db);
        var m = await scenario.SeedMastersAsync();
        var order = scenario.AddOrder(
            m.HeadOffice, new DateOnly(2026, 10, 9),
            (m.StandardProduct, 1, Standard), (m.StandardProduct, 1, Standard), (m.StandardProduct, 1, Standard));
        order.Approve();
        var firstShipment = scenario.AddShipment(order.InstructShipment([new(order.Lines[0], 1)]), m.Warehouse);
        await Db.SaveChangesAsync();
        scenario.AddSalesRecord(firstShipment.Confirm(new DateOnly(2026, 10, 9)), m.HeadOffice);
        await scenario.EndRequestAsync();
        await using var other = CreateAnotherSession();

        var mine = await Db.SalesOrders.Include(o => o.Lines).SingleAsync(o => o.Id == order.Id);
        var theirs = await other.SalesOrders.Include(o => o.Lines).SingleAsync(o => o.Id == order.Id);
        Ship(new TestScenario(Db, "A"), mine, mine.Lines.Single(l => l.Id == order.Lines[1].Id), m);
        Ship(new TestScenario(other, "B"), theirs, theirs.Lines.Single(l => l.Id == order.Lines[2].Id), m);
        Assert.Equal(SalesOrderStatus.PartiallyShipped, mine.Status); // どちらも、相手の行がまだ残っていると見ている
        Assert.Equal(SalesOrderStatus.PartiallyShipped, theirs.Status);
        await Db.SaveChangesAsync();

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => other.SaveChangesAsync());
    }

    // ── 物理削除の禁止 ──
    // 子のある親(明細のある受注)は、外部キーの RESTRICT で DB も EF Core も消させない。
    // 子の無い行(売上明細のような末端の行)は、どちらも止めないので、保存の時点で止める
    [Fact]
    public async Task 末端の行でも_削除の印を付けて保存しようとすると止まり_消えない()
    {
        var scenario = new TestScenario(Db);
        var m = await scenario.SeedMastersAsync();
        var order = scenario.AddOrder(m.HeadOffice, new DateOnly(2026, 10, 9), (m.StandardProduct, 1, Standard));
        order.Approve();
        var shipment = scenario.AddShipment(order.InstructShipment([new(order.Lines[0], 1)]), m.Warehouse);
        await Db.SaveChangesAsync();
        var record = scenario.AddSalesRecord(shipment.Confirm(new DateOnly(2026, 10, 9)), m.HeadOffice);
        await scenario.EndRequestAsync();

        var line = await Db.Set<SalesRecordLine>().SingleAsync(l => l.Id == record.Lines[0].Id);
        Db.Entry(line).State = EntityState.Deleted; // Remove を使わない書き方でも止まること

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Db.SaveChangesAsync());
        Assert.Contains("物理削除", error.Message);
        Db.ChangeTracker.Clear();
        Assert.True(await Db.Set<SalesRecordLine>().AnyAsync(l => l.Id == record.Lines[0].Id));
    }

    // そのセッションで、1行を1つ出荷して確定する(保存はしない)
    private static void Ship(TestScenario session, SalesOrder order, SalesOrderLine line, TestScenario.MasterData m)
    {
        var shipment = session.AddShipment(order.InstructShipment([new(line, 1)]), m.Warehouse);
        session.AddSalesRecord(shipment.Confirm(new DateOnly(2026, 10, 10)), m.HeadOffice);
    }

    private Task<List<AuditLog>> LogsFor(string table, long id) =>
        Db.AuditLogs.Where(l => l.EntityType == table && l.EntityId == id).OrderBy(l => l.Id).ToListAsync();

    private static Dictionary<string, JsonElement> Json(string? json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json!)!;

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }
}
