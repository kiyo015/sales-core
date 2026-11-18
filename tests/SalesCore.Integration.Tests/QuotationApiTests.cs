using System.Net;
using Microsoft.EntityFrameworkCore;
using SalesCore.Application;
using SalesCore.Domain;

namespace SalesCore.Integration.Tests;

/// <summary>
/// 見積の API(登録・明細を直す・提出・失注・受注にする)。業務ルール集「3. 販売の流れ」の「見積と得意先別単価」。
/// 「今日」は API の時計(日本時間)で決まる。見積日は 2026-12-01、有効期限は 2026-12-31。
/// </summary>
public sealed class QuotationApiTests(DatabaseFixture fixture) : ApiTest(fixture)
{
    private static readonly DateOnly QuotedOn = new(2026, 12, 1);
    private static readonly DateOnly ValidUntil = new(2026, 12, 31);

    // 日本時間のその日の 0:00 と 23:59(日付の境目)
    private static DateTimeOffset StartOf(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(9));
    private static DateTimeOffset EndOf(DateOnly day) => new(day.ToDateTime(new TimeOnly(23, 59, 59)), TimeSpan.FromHours(9));

    private static CreateQuotation Quotation(string customerCode, params CreateQuotationLine[] lines) =>
        new(customerCode, QuotedOn, ValidUntil, lines);

    private static async Task<QuotationView> Create(HttpClient api, params CreateQuotationLine[] lines) =>
        await Send<QuotationView>(api, "/quotations", Quotation("C002", lines), HttpStatusCode.Created);

    private async Task<long?> QuotationIdOf(long orderId) =>
        await Db.SalesOrders.Where(o => o.Id == orderId).Select(o => EF.Property<long?>(o, "QuotationId")).SingleAsync();

    // 支店(C002)の見積は、請求先(本社 C001)の得意先別単価を使う。単価は見積日(12/1)で決まる
    // (11/30 までは 95円、12/1〜12/15 は 90円、12/16 から 85円)。登録するのは今日(12/31)だが、今日の 85円ではない
    [Fact]
    public async Task 見積を登録すると_単価は見積日に有効な請求先の得意先別単価で_税率は見積日の税率になる()
    {
        var m = await new TestScenario(Db).SeedMastersAsync();
        var (before, _) = CustomerPrice.Register(m.HeadOffice, m.StandardProduct, 95m, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), []);
        var (from, _) = CustomerPrice.Register(m.HeadOffice, m.StandardProduct, 90m, QuotedOn, null, [before]);
        var (after, _) = CustomerPrice.Register(m.HeadOffice, m.StandardProduct, 85m, new DateOnly(2026, 12, 16), null, [before, from]);
        Db.AddRange(before, from, after);
        await Db.SaveChangesAsync();
        Now = EndOf(ValidUntil);
        var api = Api();

        var quotation = await Create(api,
            new CreateQuotationLine("P001", 3m),          // 得意先別単価 90円
            new CreateQuotationLine("P002", 1.5m),        // 得意先別単価が無いので標準単価 33.33円
            new CreateQuotationLine("P002", 1m, 30m));    // 指定した単価

        Assert.Matches(@"^QT-\d{6}$", quotation.Number);
        Assert.Equal(("Draft", "C002", QuotedOn, ValidUntil), (quotation.Status, quotation.CustomerCode, quotation.QuotedOn, quotation.ValidUntil));
        Assert.Equal(
            [("P001", 3m, 90m, 10m), ("P002", 1.5m, 33.33m, 8m), ("P002", 1m, 30m, 8m)],
            quotation.Lines.Select(l => (l.ProductCode, l.Quantity, l.UnitPrice, l.TaxRate)));
        // 270 + Round(49.995)=50 + 30 = 350円。税は 10%: 27円、8%: 80 × 8% = 6.4 → 6円
        Assert.Equal((350m, 33m), (quotation.Amount, quotation.TaxAmount));
        Assert.Equivalent(quotation, await Get<QuotationView>(api, $"/quotations/{quotation.Id}"), strict: true);
    }

    // 単価は見積のまま写し、税率は受注日(=受注にした日。日本時間)の税率で決め直す。12/15 までは 8%、12/16 から 10% の商品。
    // 見積の登録も受注化も 12/16 に行う。見積の税率は見積日(12/1)の 8%、受注の税率は受注日(12/16)の 10%
    [Fact]
    public async Task 提出して受注にすると_受注日は日本時間の今日で_単価は見積のまま_税率は受注日の税率になり_見積は受注済になる()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var changing = new TaxCategory("CHG", "改定あり");
        Db.AddRange(
            new TaxRatePeriod(changing, TaxRate.Of(8m), new DateOnly(2019, 10, 1), new DateOnly(2026, 12, 15)),
            new TaxRatePeriod(changing, TaxRate.Of(10m), new DateOnly(2026, 12, 16)),
            new Product("P900", "改定品", "個", changing, 100m));
        await Db.SaveChangesAsync();
        Now = StartOf(new DateOnly(2026, 12, 16)); // UTC では 12/15 15:00。日本時間の日付で決まる
        var api = Api();
        var quotation = await Create(api, new CreateQuotationLine("P900", 2m, 95m), new CreateQuotationLine("P001", 1m));
        await Send<QuotationView>(api, $"/quotations/{quotation.Id}/submit");
        Assert.Equal(8m, quotation.Lines[0].TaxRate);

        var order = await Send<SalesOrderView>(api, $"/quotations/{quotation.Id}/order", expected: HttpStatusCode.Created);

        Assert.Equal(("Received", "C002", new DateOnly(2026, 12, 16)), (order.Status, order.CustomerCode, order.OrderedOn));
        Assert.Equal([("P900", 2m, 95m, 10m), ("P001", 1m, 105m, 10m)], order.Lines.Select(l => (l.ProductCode, l.Quantity, l.UnitPrice, l.TaxRate)));
        Assert.Equal(quotation.Id, await QuotationIdOf(order.Id));
        Assert.Equal("Ordered", (await Get<QuotationView>(api, $"/quotations/{quotation.Id}")).Status);
    }

    [Theory]
    [InlineData("submit")]
    [InlineData("order")]
    public async Task 有効期限の当日までは提出と受注化ができ_翌日からは409(string operation)
    {
        await new TestScenario(Db).SeedMastersAsync();
        Now = EndOf(QuotedOn);
        var quotation = await Create(Api(), new CreateQuotationLine("P001", 1m));
        if (operation == "order") await Send<QuotationView>(Api(), $"/quotations/{quotation.Id}/submit");
        var expectedStatus = operation == "order" ? "Submitted" : "Draft";

        Now = StartOf(ValidUntil.AddDays(1));
        var expired = Api();
        var problem = await AssertProblem(expired, $"/quotations/{quotation.Id}/{operation}", HttpStatusCode.Conflict);
        Assert.Contains("2026-12-31", problem.Detail);
        Assert.Equal(expectedStatus, (await Get<QuotationView>(expired, $"/quotations/{quotation.Id}")).Status);

        Now = EndOf(ValidUntil);
        var response = await Api().PostAsync($"/quotations/{quotation.Id}/{operation}", null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    // 受注を取り消しても、見積は受注済のまま(状態は逆戻りしない。もう一度受注にするなら新しい見積を作る。2026-11-18 本人の決定)
    [Fact]
    public async Task 受注済の見積はもう受注にできず_受注を取り消しても受注済のまま()
    {
        await new TestScenario(Db).SeedMastersAsync();
        var api = Api();
        Now = EndOf(QuotedOn);
        var quotation = await Create(Api(), new CreateQuotationLine("P001", 1m));
        await Send<QuotationView>(Api(), $"/quotations/{quotation.Id}/submit");
        var order = await Send<SalesOrderView>(Api(), $"/quotations/{quotation.Id}/order", expected: HttpStatusCode.Created);

        await AssertProblem(Api(), $"/quotations/{quotation.Id}/order", HttpStatusCode.Conflict);
        await Send<SalesOrderView>(api, $"/sales-orders/{order.Id}/cancel");
        await AssertProblem(Api(), $"/quotations/{quotation.Id}/order", HttpStatusCode.Conflict);

        Assert.Equal("Ordered", (await Get<QuotationView>(api, $"/quotations/{quotation.Id}")).Status);
        Assert.Equal(1, await Db.SalesOrders.CountAsync(o => EF.Property<long?>(o, "QuotationId") == quotation.Id));
    }

    // 2人が同じ見積を開いて、両方が受注にした。後の方は 409 で、受注は1つだけ(見積の版数と、sales_orders.quotation_id の一意の制約)
    [Fact]
    public async Task 同じ見積を2人が同時に受注にすると_受注は1つだけで_後の方は409()
    {
        await new TestScenario(Db).SeedMastersAsync();
        Now = EndOf(QuotedOn);
        var quotation = await Create(Api(), new CreateQuotationLine("P001", 1m));
        await Send<QuotationView>(Api(), $"/quotations/{quotation.Id}/submit");

        // 2人目が見積を開いた(読み込んだ)後に、1人目が受注にする
        var stale = CreateAnotherSession();
        await stale.Quotations.Include(q => q.Customer).Include(q => q.Lines).ThenInclude(l => l.Product).ThenInclude(p => p.TaxCategory)
            .SingleAsync(q => q.Id == quotation.Id);
        await Send<SalesOrderView>(Api(), $"/quotations/{quotation.Id}/order", expected: HttpStatusCode.Created);

        await AssertProblem(Api(() => stale), $"/quotations/{quotation.Id}/order", HttpStatusCode.Conflict);
        Assert.Equal(1, await Db.SalesOrders.CountAsync(o => EF.Property<long?>(o, "QuotationId") == quotation.Id));
    }

    // 受注の登録と同じ検査(業務ルール集「見積と得意先別単価」。PR #23 の AIレビュー)。止まった時は、見積は提出済のまま
    [Theory]
    [InlineData("customer")]
    [InlineData("product")]
    [InlineData("taxRate")]
    public async Task 見積の後に得意先や商品が無効になったか_受注日の税率が無ければ_受注にできず400で_見積は提出済のまま(string what)
    {
        var m = await new TestScenario(Db).SeedMastersAsync();
        var ending = new TaxCategory("END", "12月で終わる");
        Db.AddRange(
            new TaxRatePeriod(ending, TaxRate.Of(10m), new DateOnly(2019, 10, 1), new DateOnly(2026, 12, 15)),
            new Product("P901", "終わる品", "個", ending, 100m));
        await Db.SaveChangesAsync();
        Now = EndOf(QuotedOn);
        var quotation = await Create(Api(), new CreateQuotationLine("P001", 1m), new CreateQuotationLine("P901", 1m));
        await Send<QuotationView>(Api(), $"/quotations/{quotation.Id}/submit");
        switch (what)
        {
            case "customer":
                await Db.Database.ExecuteSqlAsync($"UPDATE customers SET is_active = false WHERE id = {m.Branch.Id}");
                break;
            case "product":
                await Db.Database.ExecuteSqlAsync($"UPDATE products SET is_active = false WHERE id = {m.StandardProduct.Id}");
                break;
        }

        Now = EndOf(new DateOnly(2026, 12, what == "taxRate" ? 16 : 15)); // 税率は 12/15 まで
        var api = Api();
        await AssertProblem(api, $"/quotations/{quotation.Id}/order", HttpStatusCode.BadRequest);

        Assert.Equal("Submitted", (await Get<QuotationView>(api, $"/quotations/{quotation.Id}")).Status);
        Assert.Equal(0, await Db.SalesOrders.CountAsync(o => EF.Property<long?>(o, "QuotationId") == quotation.Id));
    }

    [Fact]
    public async Task 作成中の明細は直せて金額が変わり_提出した後は409で直せない()
    {
        await new TestScenario(Db).SeedMastersAsync();
        Now = EndOf(QuotedOn);
        var api = Api();
        var quotation = await Create(api, new CreateQuotationLine("P001", 1m));
        var line = quotation.Lines[0].Id;

        var changed = await Send<QuotationView>(api, $"/quotations/{quotation.Id}/lines/{line}", new ChangeQuotationLine(3m, 100m));
        Assert.Equal((3m, 100m, 300m), (changed.Lines[0].Quantity, changed.Lines[0].UnitPrice, changed.Amount));

        await Send<QuotationView>(api, $"/quotations/{quotation.Id}/submit");
        await AssertProblem(api, $"/quotations/{quotation.Id}/lines/{line}", HttpStatusCode.Conflict, new ChangeQuotationLine(1m, 1m));
        Assert.Equal(3m, (await Get<QuotationView>(api, $"/quotations/{quotation.Id}")).Lines[0].Quantity);
    }

    [Fact]
    public async Task 失注にした見積は_提出も受注化もできない()
    {
        await new TestScenario(Db).SeedMastersAsync();
        Now = EndOf(QuotedOn);
        var api = Api();
        var quotation = await Create(api, new CreateQuotationLine("P001", 1m));

        Assert.Equal("Lost", (await Send<QuotationView>(api, $"/quotations/{quotation.Id}/lost")).Status);
        await AssertProblem(api, $"/quotations/{quotation.Id}/submit", HttpStatusCode.Conflict);
        await AssertProblem(api, $"/quotations/{quotation.Id}/order", HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task 入力の誤りは400_無い見積は404で_理由を返す()
    {
        await new TestScenario(Db).SeedMastersAsync();
        Now = EndOf(QuotedOn);
        var api = Api();
        var other = await Create(api, new CreateQuotationLine("P001", 1m));
        var quotation = await Create(api, new CreateQuotationLine("P001", 1m));

        await AssertProblem(api, "/quotations", HttpStatusCode.BadRequest, Quotation("", new CreateQuotationLine("P001", 1m)));
        await AssertProblem(api, "/quotations", HttpStatusCode.BadRequest, Quotation("C002", new CreateQuotationLine("", 1m)));
        await AssertProblem(api, "/quotations", HttpStatusCode.BadRequest, Quotation("C002"));
        await AssertProblem(api, "/quotations", HttpStatusCode.BadRequest,
            new CreateQuotation("C002", QuotedOn, QuotedOn.AddDays(-1), [new CreateQuotationLine("P001", 1m)]));
        await AssertProblem(api, "/quotations", HttpStatusCode.BadRequest, Quotation("C002", new CreateQuotationLine("P001", 0m)));
        await AssertProblem(api, $"/quotations/{quotation.Id}/lines/{other.Lines[0].Id}", HttpStatusCode.BadRequest, new ChangeQuotationLine(2m, 1m));
        await AssertProblem(api, "/quotations/999999999/submit", HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync("/quotations/999999999")).StatusCode);
    }

    // 見積の受注化も、承認待ちかどうかは受注の登録と同じく税抜合計で決める。営業は承認待ちを承認できない
    [Fact]
    public async Task 基準額以上の見積から作った受注は承認待ちになる()
    {
        await new TestScenario(Db).SeedMastersAsync();
        Now = EndOf(QuotedOn);
        var api = Api();
        var quotation = await Create(api, new CreateQuotationLine("P001", 10_000m)); // 105円 × 1万箱 = 105万円
        await Send<QuotationView>(api, $"/quotations/{quotation.Id}/submit");

        var order = await Send<SalesOrderView>(api, $"/quotations/{quotation.Id}/order", expected: HttpStatusCode.Created);

        Assert.Equal(("PendingApproval", 1_050_000m), (order.Status, order.Amount));
    }
}
