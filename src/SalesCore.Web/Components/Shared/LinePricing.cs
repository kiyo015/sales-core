namespace SalesCore.Web.Components.Shared;

/// <summary>入力中の明細。単価は商品を選んだ時に入れ、書き換えられる。</summary>
public sealed class PricedLine
{
    public string? ProductCode { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? UnitPrice { get; set; }

    /// <summary>最後に画面が入れた単価。今の単価と違えば、利用者が手で直した。</summary>
    internal decimal? Filled { get; set; }
}

/// <summary>
/// 明細の単価の初期値を入れる(受注の登録と見積の登録で共通。業務ルール集「受注の単価と税率」)。
/// 単価は、得意先・日付・商品から、登録と同じ決め方(得意先別単価 → 標準単価)で問い合わせる。
/// <list type="bullet">
/// <item>商品を選ぶと、その明細の単価を入れ直す(別の商品の単価が残る方が誤りに気付きにくい)。</item>
/// <item>得意先・日付を変えると、商品を選んだ全明細の単価を入れ直す(商品を先に選ぶと、得意先別単価でない単価が見えたまま送れてしまう。2026-11-16)。
///   手で直した単価も入れ直し、入れ直したことを知らせる(2026-11-17 本人の決定。得意先や日付が変われば単価の前提も変わる)。</item>
/// <item>問い合わせの途中で得意先・日付・商品・単価が変わったら入れない(後から始めた方に任せる。手で直した値引きを上書きしない。PR #24 の AIレビュー)。</item>
/// <item>登録は問い合わせを待ち、待つ間に単価が変わったら送らずに見せる(<see cref="SettleAsync"/>)。</item>
/// </list>
/// </summary>
/// <param name="context">今の得意先と日付(受注日・見積日)。</param>
/// <param name="priceOf">得意先・商品・日付の単価(得意先を選んでいなければ標準単価)。</param>
/// <param name="run">問い合わせを画面の操作として流す(誤りを画面に出す。<see cref="OperationPage"/> の RunAsync)。</param>
public sealed class LinePricing(
    Func<(string? CustomerCode, DateOnly On)> context,
    Func<string?, string?, DateOnly, Task<decimal?>> priceOf,
    Func<Func<Task>, Task<bool>> run)
{
    // 問い合わせ中の単価。登録はこれが終わってから送る(得意先を変えた直後に押すと、入れ直す前の単価で受注していた。2026-11-16 の E2E)
    private readonly List<Task> _pending = [];

    public List<PricedLine> Lines { get; } = [new()];

    /// <summary>得意先・日付を変えて、手で直した単価を入れ直した。次に得意先・日付を変えるまで残る。</summary>
    public bool HandPriceReplaced { get; private set; }

    /// <summary>商品を選んだ明細の単価を入れる。</summary>
    public Task PriceAsync(PricedLine line) => Track(PriceLineAsync(line, notify: false));

    /// <summary>
    /// 得意先・日付を変えた時に、商品を選んだ全明細の単価を入れ直す。全行を1つの仕事として、最初の await より前に待ち合わせに入れる
    /// (1行ずつ入れると、登録が2行目以降を待たずに送った。PR #24 の AIレビュー2回目)。行は先に写し取る(問い合わせの途中で明細を足しても、列挙が壊れない)。
    /// 1行ずつ順に問い合わせる(同時に流すと、1本の接続を共有するテストで衝突する)
    /// </summary>
    public Task RepriceAllAsync()
    {
        HandPriceReplaced = false;
        var lines = Lines.Where(l => !string.IsNullOrEmpty(l.ProductCode)).ToList();
        return Track(RepriceAsync(lines));
    }

    /// <summary>問い合わせ中の単価を待つ。押した時に見えていた単価から変わったら false(送らずに見せる)。</summary>
    public async Task<bool> SettleAsync()
    {
        var shown = Lines.Select(l => l.UnitPrice).ToList();
        while (_pending.Count > 0) // 待つ間に始まった問い合わせも待つ
        {
            var waiting = _pending.ToArray();
            _pending.Clear();
            await Task.WhenAll(waiting);
        }
        return Lines.Select(l => l.UnitPrice).SequenceEqual(shown);
    }

    private async Task RepriceAsync(List<PricedLine> lines)
    {
        foreach (var line in lines)
        {
            await PriceLineAsync(line, notify: true);
        }
    }

    private Task PriceLineAsync(PricedLine line, bool notify) => run(async () =>
    {
        var (customerCode, on) = context();
        var asked = (customerCode, line.ProductCode, on, line.UnitPrice);
        var handEdited = line.UnitPrice is not null && line.UnitPrice != line.Filled;
        var price = await priceOf(asked.customerCode, asked.ProductCode, asked.on);
        var (customerNow, onNow) = context();
        if ((customerNow, line.ProductCode, onNow, line.UnitPrice) != asked) return;
        if (notify && handEdited && price != line.UnitPrice) HandPriceReplaced = true;
        line.UnitPrice = line.Filled = price;
    });

    private Task Track(Task task)
    {
        _pending.Add(task);
        return task;
    }
}
