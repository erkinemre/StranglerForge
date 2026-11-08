var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole();
builder.Services.AddHealthChecks();
var connection = builder.Configuration.GetConnectionString("LegacyCommerce")
    ?? throw new InvalidOperationException("ConnectionStrings:LegacyCommerce is required.");
builder.Services.AddCommerceInfrastructure(connection);
var catalogMode = builder.Configuration["CatalogOwnership:Mode"] ?? "Extracted";
if (catalogMode is "Legacy" or "ReadOnly")
{
    builder.Services.AddScoped<IProductCatalog, LegacySqlProductReader>();
}
else if (catalogMode == "Extracted")
{
    var catalogUrl = builder.Configuration["CatalogService:BaseUrl"]
        ?? throw new InvalidOperationException("CatalogService:BaseUrl is required in extracted mode.");
    builder.Services.AddHttpClient<IProductCatalog, CatalogHttpProductReader>(client =>
    {
        client.BaseAddress = new Uri(catalogUrl, UriKind.Absolute);
        client.Timeout = TimeSpan.FromSeconds(3);
    });
}
else throw new InvalidOperationException("CatalogOwnership:Mode must be Legacy, ReadOnly, or Extracted.");
var inventoryMode = builder.Configuration["InventoryOwnership:Mode"] ?? "Legacy";
if (inventoryMode == "Legacy") builder.Services.AddScoped<IInventoryGateway, LegacySqlInventoryGateway>();
else if (inventoryMode == "Extracted")
{
    var inventoryUrl = builder.Configuration["InventoryService:BaseUrl"] ?? throw new InvalidOperationException("InventoryService:BaseUrl is required.");
    builder.Services.AddHttpClient<IInventoryGateway, InventoryHttpGateway>(client => { client.BaseAddress = new Uri(inventoryUrl); client.Timeout = TimeSpan.FromSeconds(3); });
}
else throw new InvalidOperationException("InventoryOwnership:Mode must be Legacy or Extracted.");
builder.Services.AddScoped<ICorrelationIdAccessor, CorrelationIdAccessor>();
builder.Services.AddScoped<CreateCustomerHandler>();
builder.Services.AddScoped<CreateProductHandler>();
builder.Services.AddScoped<AddStockHandler>();
builder.Services.AddScoped<CreateOrderHandler>();
builder.Services.AddScoped<ProcessPaymentHandler>();
builder.Services.AddScoped<ExternalOrderBoundary>();
if (builder.Configuration["OrdersOwnership:Mode"] == "Extracted")
{
    var ordersUrl = builder.Configuration["OrdersService:BaseUrl"] ?? throw new InvalidOperationException("OrdersService:BaseUrl required.");
    builder.Services.AddHttpClient<IExtractedOrderPayment, ExtractedOrderPaymentGateway>(client => { client.BaseAddress = new Uri(ordersUrl); client.Timeout = TimeSpan.FromSeconds(5); });
}
builder.Services.AddScoped<DispatchNotificationHandler>();
builder.Services.AddScoped<CommerceQueries>();

var app = builder.Build();
app.Use(async (context, next) =>
{
    var incoming = context.Request.Headers["X-Correlation-ID"].ToString();
    var correlationId = Guid.TryParse(incoming, out var parsed) ? parsed.ToString("D") : Guid.NewGuid().ToString("D");
    context.RequestServices.GetRequiredService<ICorrelationIdAccessor>().CorrelationId = correlationId;
    context.Response.Headers["X-Correlation-ID"] = correlationId;
    try
    {
        await next(context);
    }
    catch (Exception exception) when (exception is CatalogUnavailableException or InventoryUnavailableException)
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("CatalogBoundary");
        logger.LogError(exception, "Dependency failed for {Method} {Path}", context.Request.Method, context.Request.Path.Value);
        if (context.Response.HasStarted) throw;
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new { title = "Dependency unavailable", status = 503 });
    }
});
app.MapHealthChecks("/health");
app.MapGet("/", () => Results.Ok(new { name = "LegacyCommerce", phase = 2 }));
app.MapCustomersEndpoints();
app.MapCatalogEndpoints();
app.MapInventoryEndpoints();
app.MapOrdersEndpoints();
app.MapPaymentsEndpoints();
app.MapNotificationsEndpoints();
app.MapPost("/internal/payments/{orderId:guid}/authorize", async (Guid orderId, ExternalPaymentInput input, ExternalOrderBoundary boundary, HttpContext context, CancellationToken ct) =>
{
    if (!OrdersInternalAuthorization.Allowed(context)) return Results.Unauthorized();
    var result = await boundary.AuthorizeAsync(orderId, input.Amount, input.Succeed, ct);
    return result.Error is null ? Results.Ok(new { succeeded = result.Payment!.Succeeded }) : Results.Conflict(result.Error);
});
app.MapPost("/internal/notifications/{orderId:guid}", async (Guid orderId, ExternalNotificationInput input, ExternalOrderBoundary boundary, HttpContext context, CancellationToken ct) =>
{
    if (!OrdersInternalAuthorization.Allowed(context)) return Results.Unauthorized();
    return await boundary.RecordNotificationAsync(orderId, input.CustomerId, input.Succeeded, ct) ? Results.Ok() : Results.BadRequest();
});
app.Run();

public sealed class CorrelationIdAccessor : ICorrelationIdAccessor
{
    public string? CorrelationId { get; set; }
}
public partial class Program;


public sealed record ExternalPaymentInput(decimal Amount, bool Succeed);
public sealed record ExternalNotificationInput(Guid CustomerId, bool Succeeded);
public static class OrdersInternalAuthorization
{
public static bool Allowed(HttpContext context)
{
    var expected = context.RequestServices.GetRequiredService<IConfiguration>()["OrdersInternal:Token"];
    var supplied = context.Request.Headers["X-Orders-Internal-Token"].ToString();
    return !string.IsNullOrWhiteSpace(expected) && !string.IsNullOrWhiteSpace(supplied) && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(supplied));
}}
