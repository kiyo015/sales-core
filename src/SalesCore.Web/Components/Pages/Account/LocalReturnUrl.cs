namespace SalesCore.Web.Components.Pages.Account;

/// <summary>ログインの後の戻り先。このサイトの中だけに限り、外のサイトへ飛ばす罠のリンクを防ぐ。</summary>
public static class LocalReturnUrl
{
    /// <summary>
    /// "/" で始まり、2文字目が "/" でも "\" でもなく、制御文字を含まない時だけ戻り先にする。それ以外はトップページ。
    /// 制御文字の検査は 2026-10-23 に足した。ブラウザはタブや改行を取り除くので、"/\t/evil.example" は
    /// "//evil.example"(外のサイト)になる。以前の画面の判定はこれを通していた(PR #9 の AIレビューの指摘で発見)
    /// </summary>
    public static string OrRoot(string? url) =>
        url is { Length: > 0 } u
        && u[0] == '/'
        && (u.Length == 1 || (u[1] != '/' && u[1] != '\\'))
        && !u.Any(char.IsControl)
            ? u
            : "/";
}
