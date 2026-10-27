using SalesCore.Infrastructure.Persistence;

namespace SalesCore.Application;

/// <summary>
/// ユースケースから見た、今の利用者(ログインしているか・役割)。権限の検査(<see cref="Permissions"/>)に使う。
/// 操作した人の Id(作成者・監査ログ)は、親の <see cref="ICurrentUserSource"/> から業務の DbContext が読む。
/// </summary>
public interface ICurrentUser : ICurrentUserSource
{
    bool IsAuthenticated { get; }

    bool IsInRole(string role);
}
