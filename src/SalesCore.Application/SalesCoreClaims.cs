namespace SalesCore.Application;

/// <summary>ログインした利用者に付ける、このシステムの独自の情報(クレームの種類)。</summary>
public static class SalesCoreClaims
{
    /// <summary>画面に出す名前(AspNetUsers.display_name)。ログインには使わない。</summary>
    public const string DisplayName = "sales_core_display_name";
}
