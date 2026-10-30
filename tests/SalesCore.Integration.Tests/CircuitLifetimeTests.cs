using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SalesCore.Application;
using SalesCore.Domain;

namespace SalesCore.Integration.Tests;

/// <summary>
/// 画面(Interactive Server)では、ユースケースは利用者がタブを開いている間の回線(circuit)1本の間ずっと同じものが使われる。
/// 操作をまたいで、前の操作で読んだデータが残ってはいけない(段階2の設計「DbContext の寿命」。本人の決定 2026-10-30)。
/// ここでは、同じユースケースのインスタンスで続けて操作して、回線を再現する。
/// </summary>
public class CircuitLifetimeTests(DatabaseFixture fixture) : DatabaseTest(fixture)
{
    private static readonly DateOnly Day = new(2026, 10, 30);

    // 前の操作で承認済として読んだ受注が残っていると、その後で別の人が取り消しても、取消の受注に出荷を指示できてしまう
    [Fact]
    public async Task 同じ画面で続けて操作しても_別の人の変更の後の状態で判定する()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var screen = Circuit(TestUser.With(Roles.Sales));
        var order = await screen.CreateAsync(new CreateSalesOrder("C001", Day, [new CreateSalesOrderLine("P001", 1m)]));
        await screen.ApproveAsync(order.Id);

        await Circuit(TestUser.With(Roles.Admin)).CancelAsync(order.Id); // 別の人の画面

        var instruct = () => screen.InstructShipmentAsync(order.Id, new InstructShipment("W01", [new(order.Lines[0].Id, 1m)]));
        var error = await Assert.ThrowsAsync<BusinessRuleException>(instruct);
        Assert.Contains("Cancelled", error.Message);
    }

    // 承認のボタンを2回続けて押した。1回目の保存の前に2回目が走っても、1つの DbContext を取り合う例外(500)にせず、
    // 後から保存した方を版数で止める(409)。承認は1回だけ
    [Fact]
    public async Task 同じ画面で同じ操作が重なっても_後の方は版数で止まる()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var user = TestUser.With(Roles.Sales);
        SalesOrderUseCases? screen = null;
        var secondClick = new RunOnceBeforeSave(async () => await screen!.ApproveAsync(_orderId));
        screen = new SalesOrderUseCases(new TestSessions(() => CreateAnotherSession(user, secondClick)), user, ApprovalThreshold.Default);
        _orderId = (await screen.CreateAsync(new CreateSalesOrder("C001", Day, [new CreateSalesOrderLine("P001", 1m)]))).Id;
        secondClick.Arm();

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => screen.ApproveAsync(_orderId));

        Assert.True(secondClick.Ran);
        Assert.Equal("Approved", (await screen.GetAsync(_orderId)).Status);
        var approvals = await Db.Database
            .SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM audit_logs WHERE entity_type = 'sales_orders' AND entity_id = {0} AND action = 'updated'", _orderId)
            .SingleAsync();
        Assert.Equal(1, approvals);
    }

    private long _orderId;

    // 回線1本ぶんのユースケース。本番の画面と同じく、同じインスタンスを操作をまたいで使う
    private SalesOrderUseCases Circuit(TestUser user) => new(Sessions(user), user, ApprovalThreshold.Default);

    // 準備ができてから(Arm)、最初の保存の直前に1回だけ、別の操作を割り込ませる
    private sealed class RunOnceBeforeSave(Func<Task> interrupt) : SaveChangesInterceptor
    {
        private bool _armed;

        public bool Ran { get; private set; }

        public void Arm() => _armed = true;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (_armed && !Ran)
            {
                Ran = true;
                await interrupt();
            }
            return result;
        }
    }
}
