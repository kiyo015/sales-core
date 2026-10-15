using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using SalesCore.Application;
using SalesCore.Domain;
using SalesCore.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// 接続文字列は user-secrets に置く(リポジトリに書かない)。無ければ、何をすればよいかを示して止める
var connectionString = builder.Configuration.GetConnectionString("SalesCore")
    ?? throw new InvalidOperationException(
        "接続文字列 ConnectionStrings:SalesCore が設定されていない。CLAUDE.md の「DBの準備」に従って user-secrets に設定すること。");
builder.Services.AddDbContext<SalesCoreDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddScoped<SalesOrderUseCases>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// 例外を HTTP の状態コードにする(業務の判断はしない)。業務の誤り(Domain の例外型)だけ理由を返す。
// 標準の ArgumentException・InvalidOperationException は EF Core や LINQ も投げ、内部のメッセージが見えるので 500 にする
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var (status, detail) = context.Features.Get<IExceptionHandlerFeature>()?.Error switch
    {
        NotFoundException e => (StatusCodes.Status404NotFound, e.Message),
        InvalidInputException e => (StatusCodes.Status400BadRequest, e.Message),
        BusinessRuleException e => (StatusCodes.Status409Conflict, e.Message), // 状態に合わない操作
        DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "ほかの人が先に変更した。読み直してからやり直す。"),
        BadHttpRequestException e => (e.StatusCode, e.Message), // JSON の形の誤りなど
        _ => (StatusCodes.Status500InternalServerError, "サーバーで問題が起きた。"),
    };
    await Results.Problem(detail: detail, statusCode: status).ExecuteAsync(context);
}));

app.UseHttpsRedirection();

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

app.Run();

// 結合テスト(WebApplicationFactory)から参照するため
public partial class Program;
