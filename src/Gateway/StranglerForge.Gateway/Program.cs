using StranglerForge.Gateway.MigrationControl;
using Yarp.ReverseProxy.Forwarder;
var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole();
builder.Services.AddHealthChecks();
builder.Services.AddRequestTimeouts();
_ = MigrationFlagReader.Read(builder.Configuration);
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
        (path == "/api/orders" || path == "/api/products" || path == "/api/inventory/reservations" || path.EndsWith("/stock", StringComparison.OrdinalIgnoreCase)))
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
    if (context.Request.Method == "POST")
    {
        var service = path == "/api/products" ? MigrationService.Catalog
            : path == "/api/orders" ? MigrationService.Orders
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
    string? route = null;
    string? expected = null;
    if (path == "/api/products" && context.Request.Method == "POST") { route = "catalog"; expected = flags.Catalog.Owner == DataOwner.Modern ? "catalog" : "legacy"; }
    else if (path.StartsWith("/api/products/", StringComparison.OrdinalIgnoreCase)) { route = "catalogById"; expected = "catalog"; }
    else if (path.StartsWith("/api/inventory/", StringComparison.OrdinalIgnoreCase))
    {
        route = path.Contains("/reservations/", StringComparison.OrdinalIgnoreCase)
            ? path.EndsWith("/release", StringComparison.OrdinalIgnoreCase) ? "reservationRelease" : path.EndsWith("/confirm", StringComparison.OrdinalIgnoreCase) ? "reservationConfirm" : "reservationById"
            : path.EndsWith("/reservations", StringComparison.OrdinalIgnoreCase) ? "reservationCreate" : path.EndsWith("/stock", StringComparison.OrdinalIgnoreCase) ? "inventoryStock" : "inventory";
        expected = "inventory";
    }
    else if (path == "/api/orders") { route = "orders"; expected = "orders"; }
    else if (path.StartsWith("/api/orders/", StringComparison.OrdinalIgnoreCase) && context.Request.Method == "GET") { route = "orderById"; expected = "orders"; }
    if (route is not null && !string.Equals(config[$"ReverseProxy:Routes:{route}:ClusterId"], expected, StringComparison.OrdinalIgnoreCase))
    {
        context.RequestServices.GetRequiredService<MigrationDiagnostics>().Count("ownership.conflict");
        context.Response.StatusCode = 503;
        return;
    }
    await next(context);
});
app.UseRouting();
app.UseRequestTimeouts();
app.MapHealthChecks("/health");
app.MapGet("/api/products", (HttpContext context, ControlledCatalogReads reads) => reads.HandleAsync(context)).WithOrder(-1);
app.MapMigrationControl();
app.MapReverseProxy();
app.Run();

public partial class GatewayProgram;
