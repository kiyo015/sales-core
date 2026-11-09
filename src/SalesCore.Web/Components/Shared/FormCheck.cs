using Microsoft.AspNetCore.Components.Forms;

namespace SalesCore.Web.Components.Shared;

/// <summary>
/// 画面で足す入力の検査(注釈の [Required] では書けないもの)。最初に送った時に作り、その後は欄を入れ直すたびに確かめ直す
/// (直した欄の理由が、次に送るまで残らないように。PR #16・#18 の AIレビュー)。
/// </summary>
public sealed class FormCheck
{
    private readonly EditContext _context;
    private readonly ValidationMessageStore _messages;
    private readonly Action<ValidationMessageStore> _check;

    public FormCheck(EditContext context, Action<ValidationMessageStore> check)
    {
        _context = context;
        _messages = new ValidationMessageStore(context);
        _check = check;
        context.OnFieldChanged += (_, _) => Run();
    }

    /// <summary>確かめ直す。理由が無ければ true(入力の欄が数や日付として読めない理由も含む)。</summary>
    public bool Run()
    {
        _messages.Clear();
        _check(_messages);
        // DataAnnotationsValidator の無いフォームでは、Validate() が描き直しを通知しない。ValidationSummary を描き直させる
        _context.NotifyValidationStateChanged();
        return _context.Validate();
    }
}
