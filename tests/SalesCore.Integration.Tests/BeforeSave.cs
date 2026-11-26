using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SalesCore.Integration.Tests;

/// <summary>
/// 保存の直前に、別の画面の操作を1回だけ割り込ませる(確かめた後、保存までの間に、ほかの人が先に済ませた場面の再現用)。
/// 保存は監査ログのためにもう1回呼ばれるので、2回目は何もしない。
/// </summary>
internal sealed class BeforeSave(Func<Task> action) : SaveChangesInterceptor
{
    private bool _done;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (!_done)
        {
            _done = true;
            await action();
        }
        return result;
    }
}
