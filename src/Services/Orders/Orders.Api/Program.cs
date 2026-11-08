using MassTransit;
using Microsoft.EntityFrameworkCore;
using StranglerForge.Orders.Application;
using StranglerForge.Orders.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole();
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
var connection = builder.Configuration.GetConnectionString("Orders") ?? throw new InvalidOperationException("ConnectionStrings:Orders required.");
builder.Services.AddOrdersInfrastructure(connection);
builder.Services.AddScoped<OrderCommands>();
builder.Services.AddScoped<OrderSagaRunner>();
builder.Services.AddHttpClient<IOrderDependencies, HttpOrderDependencies>(client => client.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddHostedService<OrderSagaWorker>();
builder.Services.AddHealthChecks().AddCheck<OrdersHealthCheck>("orders-database");
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<OrderTransitionAuditConsumer>();
    x.AddConfigureEndpointsCallback((context, name, cfg) => cfg.UseEntityFrameworkOutbox<OrdersDb>(context));
    x.AddEntityFrameworkOutbox<OrdersDb>(o => { o.UsePostgres(); o.UseBusOutbox(); });
    if (builder.Configuration.GetValue<bool>("RabbitMq:UseInMemory")) x.UsingInMemory((context, cfg) => cfg.ConfigureEndpoints(context));
    else x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:Host"] ?? "localhost", builder.Configuration["RabbitMq:VirtualHost"] ?? "/", h => { h.Username(builder.Configuration["RabbitMq:Username"] ?? "guest"); h.Password(builder.Configuration["RabbitMq:Password"] ?? "guest"); });
        cfg.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(2)));
        cfg.ConfigureEndpoints(context);
    });
});
var app = builder.Build();
app.Use(async (context, next) =>
{
    var incoming = context.Request.Headers["X-Correlation-ID"].ToString();
    var correlation = Guid.TryParse(incoming, out var parsed) ? parsed : Guid.NewGuid();
    context.Response.Headers["X-Correlation-ID"] = correlation.ToString("D");
    context.Request.Headers["X-Correlation-ID"] = correlation.ToString("D");
    try { await next(context); }
    catch (DependencyUnavailableException error) { app.Logger.LogWarning(error, "Order dependency unavailable for {Path}", context.Request.Path); if (!context.Response.HasStarted) { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { title = "Dependency unavailable", status = 503 }); } }
});
app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = check => check.Name == "orders-database" });
app.MapHealthChecks("/health/messaging");
app.MapPost("/api/orders", async (OrderInput input, OrderCommands commands, HttpContext context, CancellationToken ct) =>
{
    var supplied = context.Request.Headers["Idempotency-Key"].ToString();
    if (supplied.Length > 0 && !Guid.TryParse(supplied, out _)) return Results.BadRequest("Idempotency-Key must be a GUID.");
    var result = await commands.SubmitAsync(new(input.CustomerId, input.ProductId, input.Quantity, supplied.Length > 0 ? Guid.Parse(supplied) : null, Guid.Parse(context.Request.Headers["X-Correlation-ID"].ToString())), ct);
    return result.Error is null ? Results.Created($"/api/orders/{result.Order!.Id}", result.Order) : result.Error.StartsWith("Idempotency") ? Results.Conflict(result.Error) : Results.BadRequest(result.Error);
});
app.MapGet("/api/orders/{id:guid}", async (Guid id, OrderCommands commands, CancellationToken ct) => await commands.GetAsync(id, ct) is { } order ? Results.Ok(order) : Results.NotFound());
app.MapPost("/internal/orders/{id:guid}/payment-intent", async (Guid id, PaymentIntent input, IOrderStore store, IConfiguration config, HttpContext context, CancellationToken ct) =>
{
    if (!OrdersInternalAuthorization.Allowed(context, config)) return Results.Unauthorized();
    var order = await store.FindAsync(id, ct);
    if (order is null) return Results.NotFound();
    var saga = await store.FindSagaAsync(id, ct);
    if (saga?.PaymentDecision is { } prior && prior != input.Succeed) return Results.Conflict("Payment decision already recorded.");
    if (order.Status is StranglerForge.Orders.Domain.OrderStatus.Confirmed or StranglerForge.Orders.Domain.OrderStatus.Cancelled or StranglerForge.Orders.Domain.OrderStatus.Failed) return Results.Conflict("Order already processed.");
    await store.SetPaymentDecisionAsync(id, input.Succeed, ct);
    return Results.Accepted($"/api/orders/{id}", new { orderId = id, status = "PaymentPending" });
});
app.Run();

public sealed record OrderInput(Guid CustomerId, Guid ProductId, int Quantity);
public sealed record PaymentIntent(bool Succeed);
public partial class Program;
public static class OrdersInternalAuthorization
{
    public static bool Allowed(HttpContext context, IConfiguration config)
    {
        var expected = config["OrdersInternal:Token"];
        var supplied = context.Request.Headers["X-Orders-Internal-Token"].ToString();
        return !string.IsNullOrWhiteSpace(expected) && !string.IsNullOrWhiteSpace(supplied) && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(supplied));
    }
}
public sealed class OrderSagaWorker(IServiceScopeFactory scopeFactory, ILogger<OrderSagaWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { using var scope = scopeFactory.CreateScope(); await scope.ServiceProvider.GetRequiredService<OrderSagaRunner>().RunPendingAsync(stoppingToken); }
            catch (Exception error) when (error is not OperationCanceledException) { logger.LogError(error, "Order saga polling failed"); }
        }
    }
}

public sealed class OrdersHealthCheck(OrdersDb db) : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    public async Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context, CancellationToken ct = default) =>
        await db.Database.CanConnectAsync(ct) ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy() : Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("Orders PostgreSQL unavailable.");
}
public sealed class OrderTransitionAuditConsumer(OrdersDb db, ILogger<OrderTransitionAuditConsumer> logger) : IConsumer<OrderTransitionedV1>
{
    public async Task Consume(ConsumeContext<OrderTransitionedV1> context)
    {
        var message = context.Message;
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"OrderEventAudit\" (\"EventId\", \"OrderId\", \"CorrelationId\", \"Status\", \"OccurredAtUtc\") VALUES ({message.EventId}, {message.OrderId}, {message.CorrelationId}, {message.Status}, {message.OccurredAtUtc}) ON CONFLICT (\"EventId\") DO NOTHING", context.CancellationToken);
        logger.LogInformation("Consumed order transition {EventId} for order {OrderId} status {Status}", message.EventId, message.OrderId, message.Status);
    }
}
