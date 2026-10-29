using Microsoft.AspNetCore.Mvc;
using SalesCore.Application;
using SalesCore.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// 画面は Application のユースケースを同じプロセスで直接呼ぶ(段階2の設計)。DB の登録も Application に任せる。
// 接続文字列は API と同じ user-secrets(同じ UserSecretsId)から読む
var connectionString = builder.Configuration.GetConnectionString("SalesCore")
    ?? throw new InvalidOperationException(
        "接続文字列 ConnectionStrings:SalesCore が設定されていない。CLAUDE.md の「DBの準備」に従って user-secrets に設定すること。");
builder.Services.AddSalesCore(connectionString);

// ログイン(業務ルール集「8. 利用者とログイン」)。画面は _Imports.razor の [Authorize] でログインを求め、
// ログインしていなければログインの画面へ回す。例外は [AllowAnonymous] を付けた画面(ログイン・エラー)だけ。
// 全部を拒む既定の方針(FallbackPolicy)にすると、画面の部品のスクリプト(_framework/blazor.web.js)の配信まで
// ログインを求めてしまったので使わない(2026-10-23 実測)
builder.Services.AddSalesCoreIdentity(loginPath: "/account/login");
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization();

// 描画方式は Interactive Server(画面の処理はサーバーで動き、DB や業務の処理をブラウザに出さない)
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();
// 承認の基準額の設定(SalesCore:ApprovalThreshold)の誤りを、最初の受注の登録でなく起動の時に止める
_ = app.Services.GetRequiredService<ApprovalThreshold>();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery(); // フォームの送信に偽のリクエストを防ぐ印を付ける(標準機能)

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// ログアウト。フォームの送信(POST)だけを受け付け、偽のリクエストを防ぐ印(antiforgery)を確かめる。
// returnUrl は使わない。最小 API はフォームの値を受け取る引数([FromForm])がある時だけ印を確かめるので、そのために置いている
app.MapPost("/account/logout", async ([FromForm] string? returnUrl, AccountUseCases account) =>
{
    await account.SignOutAsync();
    return Results.LocalRedirect("/account/login");
});

using (var scope = app.Services.CreateScope())
{
    var account = scope.ServiceProvider.GetRequiredService<AccountUseCases>();
    // 役割(営業・経理・管理者)は、どの環境でも起動の時に用意する。無いものだけ作る
    await account.EnsureRolesAsync();

    // 開発用の初期の利用者。パスワードは各自の user-secrets(InitialUsers)にだけ置き、リポジトリには書かない。既にいれば作らない
    var initialUsers = app.Environment.IsDevelopment() ? app.Configuration.GetSection("InitialUsers").GetChildren() : [];
    foreach (var user in initialUsers)
    {
        var userName = user["UserName"];
        var password = user["Password"];
        if (string.IsNullOrEmpty(userName) || string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException(
                $"user-secrets の InitialUsers:{user.Key} に UserName か Password が無い。CLAUDE.md の「DBの準備」の4を参照。");
        }
        // 例: InitialUsers:0:Roles:0 = 管理者。Roles に1つの文字列を直接書いた時も、その1つとして扱う(黙って役割無しにしない)
        var rolesSection = user.GetSection("Roles");
        var roles = rolesSection.Value is { } single ? [single] : rolesSection.GetChildren().Select(r => r.Value!).ToList();
        await account.EnsureUserAsync(userName, user["DisplayName"] ?? userName, password, roles);
    }
}

app.Run();
