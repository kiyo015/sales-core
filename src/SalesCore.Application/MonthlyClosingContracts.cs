namespace SalesCore.Application;

/// <summary>月次締めの依頼(年月)。</summary>
public sealed record CloseMonth(int Year, int Month);

/// <summary>月次締め。請求先ごとの売掛金の残高を持つ(請求先のコードの順)。</summary>
public sealed record MonthlyClosingView(int Year, int Month, IReadOnlyList<ReceivableBalanceView> Balances);

/// <summary>売掛金の残高。期末 ＝ 期首 ＋ 売上(税抜) ＋ 消費税(締め日がその月の請求書) − 入金。負なら前受。</summary>
public sealed record ReceivableBalanceView(string BillingCustomerCode, string BillingCustomerName, decimal Opening, decimal Sales, decimal Tax, decimal Received, decimal Closing);

/// <summary>締めた月の一覧の行。期末は請求先の期末の合計。</summary>
public sealed record MonthlyClosingSummary(int Year, int Month, decimal Closing);

/// <summary>次に締める月。終わっていなければ(末日が日本時間の今日以後)まだ締められない。</summary>
public sealed record NextClosingView(int Year, int Month, bool HasEnded);

/// <summary>請求先の売掛金の残高の推移(締めた月の古い順)。期首も月の額も0の月は行が無い。</summary>
public sealed record ReceivableHistoryView(string BillingCustomerCode, string BillingCustomerName, IReadOnlyList<MonthlyBalanceView> Months);

public sealed record MonthlyBalanceView(int Year, int Month, decimal Opening, decimal Sales, decimal Tax, decimal Received, decimal Closing);
