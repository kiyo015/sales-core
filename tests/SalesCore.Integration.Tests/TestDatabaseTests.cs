namespace SalesCore.Integration.Tests;

/// <summary>
/// 結合テストが開発用DBを触らないことを確かめる。テストはデータを書いては戻すので、
/// 接続先を間違えると開発用DBのデータを壊す。DBには接続しない。
/// </summary>
public class TestDatabaseTests
{
    [Theory]
    [InlineData("Host=localhost;Database=salescore_dev;Username=u;Password=p")]
    [InlineData("Host=localhost;Database=salescore;Username=u;Password=p")]
    [InlineData("Host=localhost;Database=salescore_test_backup;Username=u;Password=p")]
    [InlineData("Host=localhost;Username=u;Password=p")]
    public void テスト用DB以外を指す接続文字列は拒否する(string connectionString)
    {
        Assert.Throws<InvalidOperationException>(() => TestDatabase.EnsureTestDatabase(connectionString));
    }

    [Fact]
    public void テスト用DBを指す接続文字列は受け入れる()
    {
        TestDatabase.EnsureTestDatabase("Host=localhost;Database=salescore_test;Username=u;Password=p");
    }
}
