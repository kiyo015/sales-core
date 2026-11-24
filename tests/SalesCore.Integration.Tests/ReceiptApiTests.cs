using System.Net;
using Microsoft.EntityFrameworkCore;
using SalesCore.Application;

namespace SalesCore.Integration.Tests;

/// <summary>
/// 入金の API(Day56 の完了条件: 経理が入金を登録すると古い請求書から消し込まれ、権限の無い役割は API でも拒まれる)。
/// 規則の定義元は docs/domain/business-rules.md「4. 請求」の「入金と消込」。
/// 今日は日本時間の 2030-12-10 0:30(UTC ではまだ 12/9)。入金日・取消の日付を UTC で決める誤りが見えるように。
/// 請求書は 10月締め 1,155円と 11月締め 2,310円(本社 C001)。11月を先に作り、Id の順と締め日の順を逆にしてある。
/// </summary>
public sealed class ReceiptApiTests(DatabaseFixture fixture) : ApiTest(fixture)
{
    private static readonly DateOnly Today = new(2030, 12, 10);
    private static readonly DateOnly ReceivedOn = new(2030, 12, 5);

    private async Task<(InvoiceView October, InvoiceView November)> AddInvoicesAsync()
    {
        Now = new DateTimeOffset(2030, 12, 10, 0, 30, 0, TimeSpan.FromHours(9));
        User = TestUser.With(Roles.Accounting);
        var scenario = new TestScenario(Db);
        var m = await scenario.SeedMastersAsync();
        var november = await scenario.AddInvoiceAsync(m, new DateOnly(2030, 11, 30), 20m);
        var october = await scenario.AddInvoiceAsync(m, new DateOnly(2030, 10, 31), 10m);
        await scenario.EndRequestAsync();
        var api = Api();
        return (await Get<InvoiceView>(api, $"/invoices/{october.Id}"), await Get<InvoiceView>(api, $"/invoices/{november.Id}"));
    }

    [Fact]
    public async Task 経理が入金を登録すると_締め日の古い請求書から消し込まれ_請求書の未回収が減る()
    {
        var (october, november) = await AddInvoicesAsync();
        Assert.Equal((0m, 1155m, 0m, 2310m), (october.AllocatedAmount, october.Outstanding, november.AllocatedAmount, november.Outstanding));
        var api = Api();

        var receipt = await Send<ReceiptView>(api, "/receipts", new RegisterReceipt("C001", Today, 1500m), HttpStatusCode.Created);

        Assert.Matches(@"^RC-\d{6}$", receipt.Number);
        Assert.Equal(("C001", Today, 1500m, 1500m, 0m, false), (receipt.BillingCustomerCode, receipt.ReceivedOn, receipt.Amount, receipt.AllocatedAmount, receipt.Unallocated, receipt.IsCancelled));
        Assert.Equal(
            [new ReceiptAllocationView(october.Id, october.Number, october.ClosingDate, 1155m), new ReceiptAllocationView(november.Id, november.Number, november.ClosingDate, 345m)],
            receipt.Allocations);
        // 照会しても同じ中身(割当は充てた順)。PostgreSQL は書き換えた行を表の後ろに置くので、10月の割当を書き換えて、
        // 順を指定しない読み込みでは11月が先に来る状態にしておく(Day54 の S6 と同じ確かめ方)
        await Db.Database.ExecuteSqlAsync($"UPDATE receipt_allocations SET amount = amount WHERE invoice_id = {october.Id}");
        var read = await Get<ReceiptView>(api, $"/receipts/{receipt.Id}");
        Assert.Equivalent(receipt, read, strict: true);
        Assert.Equal(receipt.Allocations, read.Allocations); // Equivalent は並びを見ないので、並びは別に確かめる
        var (oct, nov) = (await Get<InvoiceView>(api, $"/invoices/{october.Id}"), await Get<InvoiceView>(api, $"/invoices/{november.Id}"));
        Assert.Equal((1155m, 0m, 345m, 1965m), (oct.AllocatedAmount, oct.Outstanding, nov.AllocatedAmount, nov.Outstanding));
        Assert.Equal((1155m, 2310m), (oct.TotalAmount, nov.TotalAmount)); // 請求額は変わらない(消し込んだ額は回収の状態)
    }

    // 多い分は前受。取消は赤黒: 取り消した日(日本時間の今日)の日付で負の額の入金を立て、充てていた請求書は未回収に戻る。
    // 一覧には元の入金(取消済)と打ち消しの入金を、新しい順に両方並べる(2026-11-24 本人の決定)
    [Fact]
    public async Task 多い分は前受で残り_取り消すと今日の日付で打ち消し_一覧に両方が並ぶ()
    {
        var (october, november) = await AddInvoicesAsync();
        var api = Api();
        var receipt = await Send<ReceiptView>(api, "/receipts", new RegisterReceipt("C001", ReceivedOn, 5000m), HttpStatusCode.Created);
        Assert.Equal((3465m, 1535m), (receipt.AllocatedAmount, receipt.Unallocated));

        var reversal = await Send<ReceiptView>(api, $"/receipts/{receipt.Id}/cancel", expected: HttpStatusCode.Created);

        Assert.Equal((Today, -5000m, -3465m, -1535m, false), (reversal.ReceivedOn, reversal.Amount, reversal.AllocatedAmount, reversal.Unallocated, reversal.IsCancelled));
        Assert.Equal(new ReceiptLink(receipt.Id, receipt.Number), reversal.Reverses);
        Assert.Equal(
            [new ReceiptAllocationView(october.Id, october.Number, october.ClosingDate, -1155m), new ReceiptAllocationView(november.Id, november.Number, november.ClosingDate, -2310m)],
            reversal.Allocations);
        var original = await Get<ReceiptView>(api, $"/receipts/{receipt.Id}");
        Assert.True(original.IsCancelled);
        Assert.Equal(new ReceiptLink(reversal.Id, reversal.Number), original.ReversedBy);
        Assert.Equal((5000m, ReceivedOn), (original.Amount, original.ReceivedOn)); // 元の入金は書き換えない
        Assert.Equal((1155m, 2310m), ((await Get<InvoiceView>(api, $"/invoices/{october.Id}")).Outstanding, (await Get<InvoiceView>(api, $"/invoices/{november.Id}")).Outstanding));

        var list = await Get<List<ReceiptSummary>>(api, "/receipts");
        Assert.Equal(
            [new ReceiptSummary(reversal.Id, reversal.Number, "C001", Today, -5000m, -1535m, false, receipt.Number),
             new ReceiptSummary(receipt.Id, receipt.Number, "C001", ReceivedOn, 5000m, 1535m, true, null)],
            list.Where(r => r.Id == receipt.Id || r.Id == reversal.Id));
    }

    [Fact]
    public async Task 取り消した入金と打ち消しの入金は_取り消せない()
    {
        await AddInvoicesAsync();
        var api = Api();
        var receipt = await Send<ReceiptView>(api, "/receipts", new RegisterReceipt("C001", ReceivedOn, 1000m), HttpStatusCode.Created);
        var reversal = await Send<ReceiptView>(api, $"/receipts/{receipt.Id}/cancel", expected: HttpStatusCode.Created);

        Assert.Equal($"入金 {receipt.Id} は取り消し済み。", (await AssertProblem(api, $"/receipts/{receipt.Id}/cancel", HttpStatusCode.Conflict)).Detail);
        Assert.Contains("打ち消しの入金は取り消せない", (await AssertProblem(api, $"/receipts/{reversal.Id}/cancel", HttpStatusCode.Conflict)).Detail);
        Assert.Equal("入金 999999999 が無い。", (await AssertProblem(api, "/receipts/999999999/cancel", HttpStatusCode.NotFound)).Detail);
    }

    // 値型の項目を省くと、既定値(0001-01-01・0円)で通るか、分かりにくい理由になる(段階3 1週目の振り返り)。省いたら「入れる」よう求める
    [Fact]
    public async Task 入力の誤りは400で理由を返し_入金を作らない()
    {
        await AddInvoicesAsync();
        var api = Api();
        var before = await Db.Receipts.CountAsync();

        async Task<string?> Rejected(object body) => (await AssertProblem(api, "/receipts", HttpStatusCode.BadRequest, body)).Detail;

        Assert.Equal("入金日を入れる。", await Rejected(new { BillingCustomerCode = "C001", Amount = 1000m }));
        Assert.Equal("入金額を入れる。", await Rejected(new { BillingCustomerCode = "C001", ReceivedOn = Today }));
        Assert.Equal("入金額 1000.5 円は、円単位で入れる。", await Rejected(new RegisterReceipt("C001", Today, 1000.5m)));
        Assert.Equal("入金額 0 円は、1円以上にする。", await Rejected(new RegisterReceipt("C001", Today, 0m)));
        Assert.Equal("入金日 2030-12-11 は、今日(2030-12-10)より先にできない。", await Rejected(new RegisterReceipt("C001", Today.AddDays(1), 1000m)));
        Assert.Equal("請求先を選ぶ。", await Rejected(new RegisterReceipt("", Today, 1000m)));
        Assert.Equal("得意先コード NO-SUCH の得意先が無い。", await Rejected(new RegisterReceipt("NO-SUCH", Today, 1000m)));
        Assert.Equal("C002 の請求先は C001。入金は請求先で受ける。", await Rejected(new RegisterReceipt("C002", Today, 1000m)));
        Assert.Equal(before, await Db.Receipts.CountAsync());
    }

    // 取引をやめて無効にした請求先でも、残った売掛金の回収を記録する(2026-11-24 本人の決定。締め請求と同じ)
    [Fact]
    public async Task 無効にした請求先からも入金を受ける()
    {
        var (october, _) = await AddInvoicesAsync();
        await Db.Database.ExecuteSqlAsync($"UPDATE customers SET is_active = false WHERE code = 'C001'");

        var receipt = await Send<ReceiptView>(Api(), "/receipts", new RegisterReceipt("C001", ReceivedOn, 1155m), HttpStatusCode.Created);

        Assert.Equal([october.Id], receipt.Allocations.Select(a => a.InvoiceId));
    }

    // 入金の登録・取消は経理と管理者(業務ルール集「役割」)。営業は参照だけで、拒まれた時は何も変わらない
    [Fact]
    public async Task 営業は入金を見られるが_登録も取消もできず403で何も変わらない()
    {
        await AddInvoicesAsync();
        var receipt = await Send<ReceiptView>(Api(), "/receipts", new RegisterReceipt("C001", ReceivedOn, 1000m), HttpStatusCode.Created);
        User = TestUser.With(Roles.Sales);
        var api = Api();

        await AssertProblem(api, "/receipts", HttpStatusCode.Forbidden, new RegisterReceipt("C001", ReceivedOn, 1000m));
        await AssertProblem(api, $"/receipts/{receipt.Id}/cancel", HttpStatusCode.Forbidden);

        Assert.Equivalent(receipt, await Get<ReceiptView>(api, $"/receipts/{receipt.Id}"), strict: true);
        Assert.Contains(receipt.Id, (await Get<List<ReceiptSummary>>(api, "/receipts")).Select(r => r.Id));
        Assert.Equal(1, await Db.Receipts.CountAsync(r => r.BillingCustomer.Code == "C001"));
    }

    // 取り消した印をすり抜けて、同じ入金の打ち消しが DB まで来た時は、一意インデックスが止め、理由を「同じ入金の取消」にする(Day55 の申し送り)。
    // 別の入金(C003 の前受だけの入金)を SQL で打ち消しに見せかけ、取消の保存を一意インデックスにぶつける
    [Fact]
    public async Task 同じ入金の打ち消しがDBにあれば_一意インデックスが止めて_同じ入金の取消の409を返す()
    {
        await AddInvoicesAsync();
        var api = Api();
        var receipt = await Send<ReceiptView>(api, "/receipts", new RegisterReceipt("C001", ReceivedOn, 1000m), HttpStatusCode.Created);
        var other = await Send<ReceiptView>(api, "/receipts", new RegisterReceipt("C003", ReceivedOn, 500m), HttpStatusCode.Created);
        await Db.Database.ExecuteSqlAsync($"UPDATE receipts SET reverses_id = {receipt.Id} WHERE id = {other.Id}");

        var problem = await AssertProblem(api, $"/receipts/{receipt.Id}/cancel", HttpStatusCode.Conflict);

        Assert.Equal("同じ入金の取消は、ほかの人が先に登録した。読み直してからやり直す。", problem.Detail);
        Assert.Equal(1000m, (await Get<InvoiceView>(api, $"/invoices/{receipt.Allocations[0].InvoiceId}")).AllocatedAmount); // 充てた 1,000円は戻っていない
        Assert.False((await Get<ReceiptView>(api, $"/receipts/{receipt.Id}")).IsCancelled);
    }
}
