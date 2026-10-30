using Microsoft.Extensions.Configuration;
using SalesCore.Domain;

namespace SalesCore.Application;

/// <summary>
/// 承認の基準額(業務ルール集「承認」)。税抜合計がこの額以上の受注は、登録の時から承認待ちになり、管理者だけが承認できる。
/// 設定 <see cref="ConfigurationKey"/>(円)から読む。無ければ <see cref="Default"/>(100万円)。
/// </summary>
public sealed record ApprovalThreshold(Money Amount)
{
    public const string ConfigurationKey = "SalesCore:ApprovalThreshold";

    public static ApprovalThreshold Default { get; } = new(Money.Of(1_000_000m));

    /// <summary>
    /// 負の値や円未満の端数は設定の誤りとして止める(0 は「すべて承認待ち」として認める)。
    /// 空の値も止める。読み取りは空を「無い」として返し、黙って既定値で動くため(PR #13 の AIレビューの Minor)。数でない値は読み取りが止める
    /// </summary>
    public static ApprovalThreshold From(IConfiguration configuration)
    {
        if (configuration[ConfigurationKey] is "")
        {
            throw new InvalidOperationException($"設定 {ConfigurationKey} が空。消すか、0以上の円単位の金額にする。");
        }
        var yen = configuration.GetValue<decimal?>(ConfigurationKey);
        if (yen is null) return Default;
        if (yen < 0m || yen % 1m != 0m)
        {
            throw new InvalidOperationException($"設定 {ConfigurationKey} は0以上の円単位の金額にする(今は {yen})。");
        }
        return new(Money.Of(yen.Value));
    }
}
