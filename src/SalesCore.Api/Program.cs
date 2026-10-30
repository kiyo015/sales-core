using Microsoft.AspNetCore.Diagnostics;
using SalesCore.Application;

var builder = WebApplication.CreateBuilder(args);

// 接続文字列は user-secrets に置く(リポジトリに書かない)。無ければ、何をすればよいかを示して止める
var connectionString = builder.Configuration.GetConnectionString("SalesCore")
    ?? throw new InvalidOperationException(
        "接続文字列 ConnectionStrings:SalesCore が設定されていない。CLAUDE.md の「DBの準備」に従って user-secrets に設定すること。");
builder.Services.AddSalesCore(connectionString);
builder.Services.AddSalesCoreApiIdentity(); // ログインしてトークンを受け取り、以降の要求に付ける
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
// 承認の基準額の設定(SalesCore:ApprovalThreshold)の誤りを、最初の受注の登録でなく起動の時に止める
_ = app.Services.GetRequiredService<ApprovalThreshold>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// 例外を HTTP の状態コードにする(業務の判断はしない)。扱いは画面と同じ OperationError で決め、業務の誤りだけ理由を返す。
// 標準の ArgumentException・InvalidOperationException は EF Core や LINQ も投げ、内部のメッセージが見えるので 500 にする
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, detail) = error switch
    {
        BadHttpRequestException e => (e.StatusCode, e.Message), // JSON の形の誤りなど(API だけ)
        not null when OperationError.From(error) is { } known => (known.Status, known.Message),
        _ => (StatusCodes.Status500InternalServerError, "サーバーで問題が起きた。"),
    };
    await Problem(context, status, detail).ExecuteAsync(context);
}));

app.UseHttpsRedirection();
// トークンから利用者を読むだけ。拒むかどうかは、ユースケースの入口(Permissions.Require)の1か所で決める。
// 書かなくても WebApplication が先頭に差し込む(2026-10-28 の対照実験で、外してもテストが通った)。どこで読むかを見えるように書く
app.UseAuthentication();

var account = app.MapGroup("/account").WithTags("ログイン");
// 規則は画面と同じ(AccountUseCases)。成功したら、トークンは Identity が応答に書く
account.MapPost("/login", async (LoginRequest request, AccountUseCases useCases, HttpContext context) =>
    await useCases.SignInAsync(request.UserName, request.Password) switch
    {
        SignInOutcome.Succeeded => Results.Empty,
        SignInOutcome.LockedOut => Problem(context, StatusCodes.Status401Unauthorized, "続けて間違えたため、ロックしている。15分たってからやり直す。"),
        // 利用者名が無いのか、パスワードが違うのか、無効なのかは区別しない(業務ルール集「8. 利用者とログイン」)
        _ => Problem(context, StatusCodes.Status401Unauthorized, "利用者名かパスワードが違う。"),
    });

var orders = app.MapGroup("/sales-orders").WithTags("受注");
orders.MapPost("/", async (CreateSalesOrder request, SalesOrderUseCases useCases) =>
{
    var order = await useCases.CreateAsync(request);
    return Results.Created($"/sales-orders/{order.Id}", order);
});
orders.MapGet("/{id:long}", (long id, SalesOrderUseCases useCases) => useCases.GetAsync(id));
orders.MapPost("/{id:long}/approve", (long id, SalesOrderUseCases useCases) => useCases.ApproveAsync(id));
orders.MapPost("/{id:long}/cancel", (long id, SalesOrderUseCases useCases) => useCases.CancelAsync(id));
orders.MapPost("/{id:long}/shipments", async (long id, InstructShipment request, SalesOrderUseCases useCases) =>
{
    var shipment = await useCases.InstructShipmentAsync(id, request);
    return Results.Created($"/shipments/{shipment.Id}", shipment);
});

var shipments = app.MapGroup("/shipments").WithTags("出荷");
shipments.MapGet("/{id:long}", (long id, SalesOrderUseCases useCases) => useCases.GetShipmentAsync(id));
shipments.MapPost("/{id:long}/confirm", (long id, ConfirmShipment request, SalesOrderUseCases useCases) => useCases.ConfirmShipmentAsync(id, request));
shipments.MapPost("/{id:long}/cancel", (long id, SalesOrderUseCases useCases) => useCases.CancelShipmentAsync(id));

var invoices = app.MapGroup("/invoices").WithTags("請求");
invoices.MapPost("/", async (CloseInvoice request, InvoiceUseCases useCases) =>
{
    var invoice = await useCases.CloseAsync(request);
    return Results.Created($"/invoices/{invoice.Id}", invoice);
});
invoices.MapGet("/{id:long}", (long id, InvoiceUseCases useCases) => useCases.GetAsync(id));

app.Run();

// 401 には、求める認証の方式(Bearer のトークン)を付ける(HTTP の決まり)
static IResult Problem(HttpContext context, int status, string detail)
{
    if (status == StatusCodes.Status401Unauthorized) context.Response.Headers.WWWAuthenticate = "Bearer";
    return Results.Problem(detail: detail, statusCode: status);
}

// 結合テスト(WebApplicationFactory)から参照するため
public partial class Program;

internal sealed record LoginRequest(string? UserName, string? Password);
