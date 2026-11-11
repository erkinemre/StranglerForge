using System.Threading.RateLimiting;
using StranglerForge.Observability;
using StranglerForge.Gateway.MigrationControl;
using Yarp.ReverseProxy.Forwarder;
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddStranglerForgeTelemetry(builder.Configuration, builder.Environment, "stranglerforge-gateway");
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Services.AddHealthChecks();
builder.Services.AddRequestTimeouts();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        context.Request.Path.StartsWithSegments("/health")
            ? RateLimitPartition.GetNoLimiter("health")
            : context.Request.Path.StartsWithSegments("/internal/migration")
                ? RateLimitPartition.GetConcurrencyLimiter("admin", _ => new ConcurrencyLimiterOptions { PermitLimit = 4, QueueLimit = 0 })
                : RateLimitPartition.GetConcurrencyLimiter("api", _ => new ConcurrencyLimiterOptions { PermitLimit = 64, QueueLimit = 0 }));
});
_ = MigrationFlagReader.Read(builder.Configuration);
if (builder.Configuration.GetConnectionString("MigrationControl") is { Length: > 0 } authorityConnection)
    builder.Services.AddSingleton(new PostgresMigrationAuthority(authorityConnection));
builder.Services.AddSingleton<MigrationStateStore>();
builder.Services.AddSingleton<MigrationDiagnostics>();
builder.Services.AddSingleton<ControlledCatalogReads>();
builder.Services.AddHttpClient("migration-read");
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("GatewayRequests");
app.Use(async (context, next) =>
{
    context.Request.Headers.Remove("Forwarded");
    var supplied = context.Request.Headers["X-Correlation-ID"].ToString();
    var correlationId = Guid.TryParse(supplied, out var parsed) ? parsed.ToString("D") : Guid.NewGuid().ToString("D");
    context.Request.Headers["X-Correlation-ID"] = correlationId;
    context.Response.Headers["X-Correlation-ID"] = correlationId;
    using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
    {
        await next(context);
        if (context.Response.StatusCode >= 500)
            logger.LogError("Gateway {Method} {Path} returned {StatusCode}",
                context.Request.Method, context.Request.Path.Value, context.Response.StatusCode);
        else
            logger.LogInformation("Gateway {Method} {Path} returned {StatusCode}",
                context.Request.Method, context.Request.Path.Value, context.Response.StatusCode);
    }
});
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    await next(context);
    if (context.GetForwarderErrorFeature() is not null && !context.Response.HasStarted
        && context.Response.StatusCode is 502 or 503 or 504)
    {
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "about:blank",
            title = "Legacy destination unavailable",
            status = context.Response.StatusCode
        }));
    }
});
app.Use(async (context, next) =>
{
    MigrationFlags flags;
    try { flags = MigrationFlagReader.Read(context.RequestServices.GetRequiredService<IConfiguration>()); }
    catch (Exception error) when (error is InvalidOperationException or FormatException)
    {
        context.RequestServices.GetRequiredService<MigrationDiagnostics>().Count("configuration.invalid");
        context.Response.StatusCode = 503;
        return;
    }
    var path = context.Request.Path.Value ?? "";
    if (flags.EmergencyPause && context.Request.Method == "POST" &&
        (path.Equals("/api/orders", StringComparison.OrdinalIgnoreCase) || path.Equals("/api/products", StringComparison.OrdinalIgnoreCase) || path.Equals("/api/inventory/reservations", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/stock", StringComparison.OrdinalIgnoreCase)))
    {
        context.RequestServices.GetRequiredService<MigrationDiagnostics>().Count("migration.paused");
        context.Response.StatusCode = 503;
        return;
    }
    if ((path.StartsWith("/api/inventory/", StringComparison.OrdinalIgnoreCase) && flags.Inventory.Owner != DataOwner.Modern) ||
        (path.StartsWith("/api/orders", StringComparison.OrdinalIgnoreCase) && !path.EndsWith("/payment", StringComparison.OrdinalIgnoreCase) && flags.Orders.Owner != DataOwner.Modern))
    {
        context.RequestServices.GetRequiredService<MigrationDiagnostics>().Count("ownership.conflict");
        context.Response.StatusCode = 503;
        return;
    }
    var states = context.RequestServices.GetRequiredService<MigrationStateStore>();
    MigrationAuthorityLease? lease = null;
    try
    {
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) lease = await states.GuardAsync(flags, context.RequestAborted);
    }
    catch (Exception error) when (error is Npgsql.NpgsqlException or InvalidOperationException or TimeoutException)
    {
        context.RequestServices.GetRequiredService<MigrationDiagnostics>().Count("authority.unavailable");
        context.Response.StatusCode = 503;
        return;
    }
    await using var authorityLease = lease;
    if (context.Request.Method == "POST")
    {
        var service = path.Equals("/api/products", StringComparison.OrdinalIgnoreCase) ? MigrationService.Catalog
            : path.Equals("/api/orders", StringComparison.OrdinalIgnoreCase) ? MigrationService.Orders
            : path.StartsWith("/api/inventory/", StringComparison.OrdinalIgnoreCase) &&
              (path.EndsWith("/stock", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/reservations", StringComparison.OrdinalIgnoreCase)) ? MigrationService.Inventory
            : (MigrationService?)null;
        if (service is { } controlled)
        {
            var stage = states.Get(controlled).Stage;
            var owner = flags.For(controlled).Owner;
            var valid = owner == DataOwner.Modern ? stage == MigrationStage.ModernActive
                : controlled == MigrationService.Catalog && stage is MigrationStage.LegacyActive or MigrationStage.ShadowValidation or MigrationStage.CanaryReady;
            if (!valid)
            {
                context.RequestServices.GetRequiredService<MigrationDiagnostics>().Count("ownership.conflict");
                context.Response.StatusCode = 503;
                return;
            }
        }
    }
    var config = context.RequestServices.GetRequiredService<IConfiguration>();
    if (flags.Catalog.Owner == DataOwner.Legacy && path.StartsWith("/api/products/", StringComparison.OrdinalIgnoreCase))
    {
        context.RequestServices.GetRequiredService<MigrationDiagnostics>().Count("ownership.conflict");
        context.Response.StatusCode = 503;
        return;
    }
    string? route = null;
    string? expected = null;
    if (path.Equals("/api/products", StringComparison.OrdinalIgnoreCase) && context.Request.Method == "POST") { route = "catalog"; expected = flags.Catalog.Owner == DataOwner.Modern ? "catalog" : "legacy"; }
    else if (path.StartsWith("/api/products/", StringComparison.OrdinalIgnoreCase)) { route = "catalogById"; expected = "catalog"; }
    else if (path.StartsWith("/api/inventory/", StringComparison.OrdinalIgnoreCase))
    {
        route = path.Contains("/reservations/", StringComparison.OrdinalIgnoreCase)
            ? path.EndsWith("/release", StringComparison.OrdinalIgnoreCase) ? "reservationRelease" : path.EndsWith("/confirm", StringComparison.OrdinalIgnoreCase) ? "reservationConfirm" : "reservationById"
            : path.EndsWith("/reservations", StringComparison.OrdinalIgnoreCase) ? "reservationCreate" : path.EndsWith("/stock", StringComparison.OrdinalIgnoreCase) ? "inventoryStock" : "inventory";
        expected = "inventory";
    }
    else if (path.Equals("/api/orders", StringComparison.OrdinalIgnoreCase)) { route = "orders"; expected = "orders"; }
    else if (path.StartsWith("/api/orders/", StringComparison.OrdinalIgnoreCase) && context.Request.Method == "GET") { route = "orderById"; expected = "orders"; }
    if (route is not null && !string.Equals(config[$"ReverseProxy:Routes:{route}:ClusterId"], expected, StringComparison.OrdinalIgnoreCase))
    {
        context.RequestServices.GetRequiredService<MigrationDiagnostics>().Count("ownership.conflict");
        context.Response.StatusCode = 503;
        return;
    }
    if (path.Equals("/api/products", StringComparison.OrdinalIgnoreCase) && context.Request.Method == "GET" && flags.Catalog.Owner == DataOwner.Legacy)
    {
        var result = await context.RequestServices.GetRequiredService<ControlledCatalogReads>().HandleAsync(context);
        await result.ExecuteAsync(context);
        return;
    }
    if (path.Equals("/api/products", StringComparison.OrdinalIgnoreCase) && context.Request.Method == "GET" &&
        !string.Equals(config["ReverseProxy:Routes:catalog:ClusterId"], "catalog", StringComparison.OrdinalIgnoreCase))
    {
        context.RequestServices.GetRequiredService<MigrationDiagnostics>().Count("ownership.conflict");
        context.Response.StatusCode = 503;
        return;
    }
    await next(context);
});
app.UseRouting();
app.UseRequestTimeouts();
app.UseDevelopmentFaultInjection(app.Environment, app.Configuration);
app.MapHealthChecks("/health");
app.MapGet("/health/live", () => Results.Ok());
app.MapGet("/health/ready", async (MigrationStateStore states, IConfiguration config, CancellationToken ct) =>
{
    try
    {
        await using var lease = await states.GuardAsync(MigrationFlagReader.Read(config), ct);
        return Results.Ok();
    }
    catch (Exception error) when (error is Npgsql.NpgsqlException or InvalidOperationException or TimeoutException) { return Results.StatusCode(503); }
});
app.MapMigrationControl();
app.MapReverseProxy();
app.Run();

public partial class GatewayProgram;
