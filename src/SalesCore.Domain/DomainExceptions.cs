namespace SalesCore.Domain;

// 業務の誤りを表す例外。API はこの2つだけを利用者に理由付きで返す(それ以外の例外はプログラムの誤りとして 500 にし、中身を返さない)。
// 標準の例外を継承するのは、意味(状態に合わない操作・不正な引数)をそのまま保つため

/// <summary>業務の決まりに反する操作(状態に合わない、受注数量を超える出荷など)。API は 409 にする。</summary>
public sealed class BusinessRuleException(string message) : InvalidOperationException(message);

/// <summary>入力が業務の決まりに合わない(数量が0以下、桁が多い、無いコードなど)。API は 400 にする。</summary>
public sealed class InvalidInputException(string message, string? paramName = null) : ArgumentException(message, paramName);
