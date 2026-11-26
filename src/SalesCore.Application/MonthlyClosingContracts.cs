namespace SalesCore.Application;

/// <summary>月次締め。請求先ごとの売掛金の残高を持つ(請求先のコードの順)。</summary>
public sealed record MonthlyClosingView(int Year, int Month, IReadOnlyList<ReceivableBalanceView> Balances);

/// <summary>売掛金の残高。期末 ＝ 期首 ＋ 売上(税抜) ＋ 消費税(締め日がその月の請求書) − 入金。負なら前受。</summary>
public sealed record ReceivableBalanceView(string BillingCustomerCode, decimal Opening, decimal Sales, decimal Tax, decimal Received, decimal Closing);
