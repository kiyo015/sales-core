using SalesCore.Application;
using SalesCore.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// 画面は Application のユースケースを同じプロセスで直接呼ぶ(段階2の設計)。DB の登録も Application に任せる。
// 接続文字列は API と同じ user-secrets(同じ UserSecretsId)から読む
var connectionString = builder.Configuration.GetConnectionString("SalesCore")
    ?? throw new InvalidOperationException(
        "接続文字列 ConnectionStrings:SalesCore が設定されていない。CLAUDE.md の「DBの準備」に従って user-secrets に設定すること。");
builder.Services.AddSalesCore(connectionString);

// 描画方式は Interactive Server(画面の処理はサーバーで動き、DB や業務の処理をブラウザに出さない)
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery(); // フォームの送信に偽のリクエストを防ぐ印を付ける(標準機能)

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
