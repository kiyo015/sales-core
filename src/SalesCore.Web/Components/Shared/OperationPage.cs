using Microsoft.AspNetCore.Components;
using SalesCore.Application;

namespace SalesCore.Web.Components.Shared;

/// <summary>
/// ユースケースを呼ぶ画面の基底。誤りは API と同じ扱い(<see cref="OperationError"/>。400・403・404・409)で、
/// 理由を <see cref="ErrorMessage"/> に出す。業務の誤りでない例外は中身を見せず、記録だけ残す。
/// </summary>
public abstract class OperationPage : ComponentBase
{
    /// <summary>業務の誤りでない例外の時に出す文言(API の 500 と同じ)。</summary>
    public static readonly OperationError Unexpected = new(500, "サーバーで問題が起きた。");

    [Inject]
    private ILogger<OperationPage> Logger { get; set; } = null!;

    /// <summary>最後の操作の誤り。成功すれば null に戻る。</summary>
    protected OperationError? Error { get; private set; }

    /// <summary>操作を呼ぶ。成功すれば true。誤りは <see cref="Error"/> に入れ、例外を画面の外に出さない。</summary>
    protected async Task<bool> RunAsync(Func<Task> operation)
    {
        Error = null;
        try
        {
            await operation();
            return true;
        }
        catch (Exception e) when (OperationError.From(e) is { } known)
        {
            Error = known;
        }
        catch (Exception e)
        {
            Logger.LogError(e, "画面の操作で、業務の誤りでない例外が起きた。");
            Error = Unexpected;
        }
        return false;
    }
}
