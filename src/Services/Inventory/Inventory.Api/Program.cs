using StranglerForge.Observability;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StranglerForge.Inventory.Application;
using StranglerForge.Inventory.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddStranglerForgeTelemetry(builder.Configuration, builder.Environment, "inventory-service");
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
var connection = builder.Configuration.GetConnectionString("Inventory") ?? throw new InvalidOperationException("ConnectionStrings:Inventory is required.");
builder.Services.AddInventoryInfrastructure(connection);
builder.Services.AddOutboxMetrics<InventoryDb>("inventory");
builder.Services.AddScoped<InventoryUseCases>();
builder.Services.AddScoped<IInventoryCorrelation, InventoryCorrelation>();
builder.Services.AddHttpClient<IInventoryProductCatalog, InventoryCatalogHttpLookup>(client => { client.BaseAddress = new Uri(builder.Configuration["CatalogService:BaseUrl"] ?? "http://127.0.0.1:5082/"); client.Timeout = TimeSpan.FromSeconds(7); }).AddSafeReadResilience();
builder.Services.AddHealthChecks().AddCheck<InventoryDatabaseHealthCheck>("inventory-database");
var rabbit = builder.Configuration["RabbitMq:Host"] ?? "localhost";
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<InventoryReservedAuditConsumer>();
    x.AddConfigureEndpointsCallback((context, name, cfg) => cfg.UseEntityFrameworkOutbox<InventoryDb>(context));
    x.AddEntityFrameworkOutbox<InventoryDb>(o => { o.UsePostgres(); o.UseBusOutbox(); });
    if (!builder.Configuration.GetValue<bool>("RabbitMq:UseInMemory")) x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(rabbit, "/", h => { h.Username(builder.Configuration["RabbitMq:Username"] ?? "guest"); h.Password(builder.Configuration["RabbitMq:Password"] ?? "guest"); });
        cfg.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(2)));
        cfg.ConfigureEndpoints(context);
    });
    else x.UsingInMemory((context, cfg) => cfg.ConfigureEndpoints(context));
});
var app = builder.Build();
app.Use(async (context, next) =>
{
    var supplied = context.Request.Headers["X-Correlation-ID"].ToString();
    var correlation = Guid.TryParse(supplied, out var parsed) ? parsed : Guid.NewGuid();
    context.RequestServices.GetRequiredService<IInventoryCorrelation>().CorrelationId = correlation;
    context.Response.Headers["X-Correlation-ID"] = correlation.ToString("D");
    context.Request.Headers["X-Correlation-ID"] = correlation.ToString("D");
    using var scope = app.Logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlation.ToString("D"), ["ServiceName"] = "inventory-service" });
    try { await next(context); }
    catch (Exception exception) when (exception is DbUpdateException or Npgsql.NpgsqlException or InventoryCatalogUnavailableException)
    {
        Telemetry.DependencyFailures.Add(1, new KeyValuePair<string, object?>("service", "inventory"));
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("InventoryDatabase").LogError(exception, "Inventory database unavailable");
        if (context.Response.HasStarted) throw;
        context.Response.StatusCode = 503;
        await context.Response.WriteAsJsonAsync(new { title = "Inventory unavailable", status = 503 });
    }
});
app.UseDevelopmentFaultInjection(app.Environment, app.Configuration);
app.MapHealthChecks("/health");
app.MapGet("/health/live", () => Results.Ok());
app.MapHealthChecks("/health/ready");
app.MapGet("/api/inventory/{productId:guid}", async (Guid productId, InventoryUseCases useCases, CancellationToken ct) =>
    await useCases.GetStockAsync(productId, ct) is { } stock ? Results.Ok(new { productId, quantity = stock.Available }) : Results.NotFound());
app.MapPost("/api/inventory/{productId:guid}/stock", async (Guid productId, StockInput input, InventoryUseCases useCases, CancellationToken ct) =>
{
    if (input.Quantity <= 0) return Results.BadRequest("Quantity must be positive.");
    var result = await useCases.AdjustAsync(productId, input.Quantity, "API stock addition", ct);
    return result.Succeeded ? Results.Ok(new { productId, quantity = result.Value!.Available }) : result.Error == "Product does not exist." ? Results.NotFound() : Results.Conflict(result.Error);
}).RequireInventoryWriteToken("Inventory:StockAdminToken");
app.MapPost("/api/inventory/reservations", async (ReserveInput input, InventoryUseCases useCases, CancellationToken ct) =>
{
    var result = await useCases.ReserveAsync(new ReserveStock(input.ReservationId, input.OrderId, input.ProductId, input.Quantity), ct);
    return result.Succeeded ? Results.Ok(result.Value) : Results.Conflict(result.Error);
}).RequireInventoryWriteToken("Inventory:ReservationToken");
app.MapGet("/api/inventory/reservations/{id:guid}", async (Guid id, InventoryUseCases useCases, CancellationToken ct) =>
    await useCases.GetReservationAsync(id, ct) is { } reservation ? Results.Ok(reservation) : Results.NotFound());
app.MapPost("/api/inventory/reservations/{id:guid}/release", async (Guid id, InventoryUseCases useCases, CancellationToken ct) =>
{
    var result = await useCases.ReleaseAsync(id, ct);
    return result.Succeeded ? Results.Ok(result.Value) : result.Error == "Reservation does not exist." ? Results.NotFound() : Results.Conflict(result.Error);
}).RequireInventoryWriteToken("Inventory:ReservationToken");
app.MapPost("/api/inventory/reservations/{id:guid}/confirm", async (Guid id, InventoryUseCases useCases, CancellationToken ct) =>
{
    var result = await useCases.ConfirmAsync(id, ct);
    return result.Succeeded ? Results.Ok(result.Value) : result.Error == "Reservation does not exist." ? Results.NotFound() : Results.Conflict(result.Error);
}).RequireInventoryWriteToken("Inventory:ReservationToken");
if (builder.Configuration.GetValue<bool>("Inventory:DiagnosticsEnabled"))
    app.MapGet("/internal/inventory/outbox", async (InventoryDb db, CancellationToken ct) => Results.Ok(new { pending = await db.Set<MassTransit.EntityFrameworkCoreIntegration.OutboxMessage>().CountAsync(ct) }));
app.Run();

public sealed class InventoryCorrelation : IInventoryCorrelation { public Guid CorrelationId { get; set; } }
public sealed record StockInput(int Quantity);
public sealed record ReserveInput(Guid ReservationId, Guid OrderId, Guid ProductId, int Quantity);
public partial class InventoryProgram;
public sealed class InventoryDatabaseHealthCheck(InventoryDb db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await db.Database.CanConnectAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Inventory PostgreSQL unavailable.");
}
public sealed class InventoryReservedAuditConsumer(InventoryDb db, ILogger<InventoryReservedAuditConsumer> logger) : IConsumer<InventoryReservedV1>
{
    public async Task Consume(ConsumeContext<InventoryReservedV1> context)
    {
        try
        {
            var message = context.Message;
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"InventoryAudit\" (\"EventId\", \"ReservationId\", \"Kind\", \"OccurredAtUtc\") VALUES ({message.EventId}, {message.ReservationId}, {"Reserved"}, {message.OccurredAtUtc}) ON CONFLICT (\"EventId\") DO NOTHING", context.CancellationToken);
            Telemetry.ConsumerProcessed.Add(1, new KeyValuePair<string, object?>("service", "inventory"));
            logger.LogInformation("Processed inventory event {EventId} for reservation {ReservationId}", message.EventId, message.ReservationId);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Telemetry.ConsumerFailures.Add(1, new KeyValuePair<string, object?>("service", "inventory"));
            throw;
        }
    }
}