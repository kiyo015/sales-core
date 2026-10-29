using System.Reflection;
using SalesCore.Application;
using SalesCore.Domain;

namespace SalesCore.Integration.Tests;

/// <summary>
/// 役割ごとにできる操作(業務ルール集「8.」の「役割」の表)を、ユースケースの入口で強制していることを確かめる。
/// 期待は表(business-rules.md)をそのまま読んで作る。表を変えればテストの期待も変わり、コードとずれれば落ちる。
/// 役割の組み合わせ × ユースケースの全組み合わせを流し、食い違いをまとめて報告する。
/// </summary>
public class PermissionTests(DatabaseFixture fixture) : DatabaseTest(fixture)
{
    private const long NoSuchId = 999_999_999;
    private static readonly DateOnly Day = new(2026, 10, 27);

    // 表の行 → その操作にあたるユースケース。権限の検査を通れば、存在しない id や入力の誤りで別の例外になる(それで「通った」と分かる)
    // 承認待ちの受注を呼ぶたびに作るので、インスタンスのプロパティにしている
    private Dictionary<string, (string Name, Func<SalesOrderUseCases, InvoiceUseCases, Task> Call)[]> UseCasesByRow => new()
    {
        ["受注・出荷・請求書の参照"] =
        [
            ("SalesOrderUseCases.GetAsync", (s, _) => s.GetAsync(NoSuchId)),
            ("SalesOrderUseCases.GetShipmentAsync", (s, _) => s.GetShipmentAsync(NoSuchId)),
            ("InvoiceUseCases.GetAsync", (_, i) => i.GetAsync(NoSuchId)),
        ],
        ["受注の登録・取消"] =
        [
            ("SalesOrderUseCases.CreateAsync", (s, _) => s.CreateAsync(new CreateSalesOrder("NO-SUCH", Day, []))),
            ("SalesOrderUseCases.CancelAsync", (s, _) => s.CancelAsync(NoSuchId)),
        ],
        ["受注の承認（税抜合計100万円未満）"] =
        [
            ("SalesOrderUseCases.ApproveAsync", (s, _) => s.ApproveAsync(NoSuchId)),
        ],
        // 承認待ち(登録の時に基準額以上)の受注を承認する。管理者が承認すると承認済になり、次の利用者で確かめられないので、毎回作る
        ["受注の承認（税抜合計100万円以上）"] =
        [
            ("SalesOrderUseCases.ApproveAsync", async (s, _) => await s.ApproveAsync(await NewPendingOrderAsync())),
        ],
        ["出荷の指示・確定・取消"] =
        [
            ("SalesOrderUseCases.InstructShipmentAsync", (s, _) => s.InstructShipmentAsync(NoSuchId, new InstructShipment("NO-SUCH", []))),
            ("SalesOrderUseCases.ConfirmShipmentAsync", (s, _) => s.ConfirmShipmentAsync(NoSuchId, new ConfirmShipment(Day))),
            ("SalesOrderUseCases.CancelShipmentAsync", (s, _) => s.CancelShipmentAsync(NoSuchId)),
        ],
        ["締め請求"] =
        [
            ("InvoiceUseCases.CloseAsync", (_, i) => i.CloseAsync(new CloseInvoice("NO-SUCH", Day))),
        ],
    };

    // 表にあるが、まだユースケースが無い行(作った日に UseCasesByRow へ移す)
    private static readonly string[] RowsWithoutUseCases =
    [
        "マスタ（得意先・商品・倉庫）・売上の参照", // 段階2の Day41 以降
        "監査ログの参照",
        "返品", // 返品の API・画面はまだ無い
        "マスタ（得意先・商品・倉庫）の登録・変更", // Day41
        "利用者の管理",
    ];

    private static readonly TestUser[] Users =
    [
        TestUser.With(Roles.Sales),
        TestUser.With(Roles.Accounting),
        TestUser.With(Roles.Admin),
        TestUser.With(Roles.Sales, Roles.Accounting), // 1人に複数の役割(できる操作は合計)
        TestUser.With(), // ログインしているが役割が無い
        TestUser.Anonymous,
    ];

    [Fact]
    public async Task 役割と操作の全組み合わせが_業務ルール集の表のとおりに許され_拒まれる()
    {
        var table = RoleTable.Read();
        var mismatches = new List<string>();
        var checkedCount = 0;

        foreach (var (row, useCases) in UseCasesByRow)
        {
            Assert.True(table.ContainsKey(row), $"業務ルール集の表に「{row}」の行が無い");
            foreach (var user in Users)
            {
                var expected = !user.IsAuthenticated ? Outcome.Unauthenticated
                    : table[row].Any(user.IsInRole) ? Outcome.Allowed
                    : Outcome.Forbidden;
                foreach (var (name, call) in useCases)
                {
                    var actual = await OutcomeOf(() => call(new SalesOrderUseCases(Db, user, ApprovalThreshold.Default), new InvoiceUseCases(Db, TimeProvider.System, user)));
                    checkedCount++;
                    if (actual != expected) mismatches.Add($"{user} × {name}(「{row}」): 期待 {expected}、実際 {actual}");
                }
            }
        }

        Assert.Empty(mismatches);
        Assert.Equal(UseCasesByRow.Values.Sum(v => v.Length) * Users.Length, checkedCount);
    }

    // 表に行を足したのにテストに対応が無い、または対応を書いたのに表に行が無い、を止める
    [Fact]
    public void 表の行は_すべてユースケースへの対応か_まだ無い行の一覧のどちらかにある()
    {
        var rows = RoleTable.Read().Keys.Order().ToList();

        Assert.Equal(rows, UseCasesByRow.Keys.Concat(RowsWithoutUseCases).Order().ToList());
    }

    // 権限の検査を掛けない公開メソッド(ログインそのものと、起動時の処理)。画面から呼ぶものはここに書かない
    private static readonly string[] MethodsWithoutPermission =
    [
        "AccountUseCases.SignInAsync", // ログインする前に呼ぶ
        "AccountUseCases.SignOutAsync",
        "AccountUseCases.EnsureRolesAsync", // 起動時(Web/Program.cs)
        "AccountUseCases.EnsureUserAsync",
        "AccountUseCases.CreateUserAsync", // 起動時の EnsureUserAsync から。利用者の管理画面を作る日に「利用者の管理」の行へ移す
    ];

    // ユースケースの公開メソッドを足したのに、ここで確かめていない(=どの行に当たるか決めていない)、を止める。
    // 新しいユースケースのクラスも拾うため、名前が UseCases で終わる公開クラスをすべて集める(PR #11 の AIレビューの Important)
    [Fact]
    public void ユースケースの公開メソッドは_すべて表のどれかの行に対応している()
    {
        var publicMethods = typeof(SalesOrderUseCases).Assembly.GetExportedTypes()
            .Where(type => type.Name.EndsWith("UseCases", StringComparison.Ordinal))
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(m => $"{type.Name}.{m.Name}"))
            .Order()
            .ToList();

        // 承認は金額で2つの行に当たるので、名前を重ねない
        Assert.Equal(publicMethods, UseCasesByRow.Values.SelectMany(v => v).Select(u => u.Name).Distinct().Concat(MethodsWithoutPermission).Order().ToList());
    }

    // 表に無い操作は管理者だけ(業務ルール集「役割」)。今は全操作が表にあるので、無い操作を作って確かめる(PR #11 の AIレビューの Minor)
    [Fact]
    public void 表に無い操作は_管理者だけができる()
    {
        const Operation unlisted = (Operation)int.MaxValue;

        foreach (var user in Users.Where(u => u.IsAuthenticated))
        {
            var allowed = Record.Exception(() => Permissions.Require(user, unlisted)) is null;
            Assert.True(allowed == user.IsInRole(Roles.Admin), $"{user}: 許された={allowed}");
        }
        Assert.Throws<UnauthenticatedException>(() => Permissions.Require(TestUser.Anonymous, unlisted));
    }

    private TestScenario? _scenario;
    private TestScenario.MasterData? _masters;

    // ボルト(105円)1万箱 = 税抜 1,050,000円。基準額(既定の100万円)以上なので承認待ちで始まる
    private async Task<long> NewPendingOrderAsync()
    {
        _scenario ??= new TestScenario(Db);
        _masters ??= await _scenario.SeedMastersAsync();
        var order = _scenario.AddOrder(_masters.HeadOffice, Day, (_masters.StandardProduct, 10_000m, TaxRate.Of(10m)));
        await Db.SaveChangesAsync();
        return order.Id;
    }

    private enum Outcome { Allowed, Forbidden, Unauthenticated }

    private static async Task<Outcome> OutcomeOf(Func<Task> call)
    {
        try
        {
            await call();
            return Outcome.Allowed;
        }
        catch (UnauthenticatedException)
        {
            return Outcome.Unauthenticated;
        }
        catch (ForbiddenException)
        {
            return Outcome.Forbidden;
        }
        catch (Exception)
        {
            return Outcome.Allowed; // 権限の検査は通り、その先(無い・入力の誤り)で止まった
        }
    }

    /// <summary>業務ルール集の「役割」の表を読む。行 → その操作ができる役割。</summary>
    private static class RoleTable
    {
        private const string Header = "| 操作 | 営業 | 経理 | 管理者 |";

        public static Dictionary<string, string[]> Read()
        {
            var lines = File.ReadAllLines(FindBusinessRules());
            var start = Array.FindIndex(lines, l => l.Trim() == Header);
            Assert.True(start >= 0, $"業務ルール集に役割の表({Header})が無い");
            var roles = new[] { Roles.Sales, Roles.Accounting, Roles.Admin };

            return lines.Skip(start + 2)
                .TakeWhile(l => l.TrimStart().StartsWith('|'))
                .Select(l => l.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray())
                .ToDictionary(
                    cells => cells[0],
                    cells => roles.Where((_, i) => cells[i + 1] == "○").ToArray());
        }

        private static string FindBusinessRules()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var path = Path.Combine(dir.FullName, "docs", "domain", "business-rules.md");
                if (File.Exists(path)) return path;
            }
            throw new FileNotFoundException("docs/domain/business-rules.md が見つからない");
        }
    }
}
