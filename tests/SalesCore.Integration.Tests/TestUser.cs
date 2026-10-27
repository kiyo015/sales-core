using SalesCore.Application;

namespace SalesCore.Integration.Tests;

/// <summary>テストの利用者。役割を決めて作る。<see cref="Anonymous"/> はログインしていない利用者。</summary>
public sealed class TestUser(string? userId, params string[] roles) : ICurrentUser
{
    public static TestUser Anonymous { get; } = new(null);

    public static TestUser With(params string[] roles) => new("test-user-" + string.Join("-", roles), roles);

    public string? UserId => userId;

    public bool IsAuthenticated => userId is not null;

    public bool IsInRole(string role) => IsAuthenticated && roles.Contains(role);

    public override string ToString() => IsAuthenticated ? (roles.Length == 0 ? "役割なし" : string.Join("+", roles)) : "未ログイン";
}
