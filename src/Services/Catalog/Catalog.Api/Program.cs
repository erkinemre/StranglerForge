using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StranglerForge.Catalog.Application;
using StranglerForge.Catalog.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole();
var connection = builder.Configuration.GetConnectionString("Catalog")
    ?? throw new InvalidOperationException("ConnectionStrings:Catalog is required.");
builder.Services.AddCatalogInfrastructure(connection);
builder.Services.AddScoped<CreateProductHandler>();
builder.Services.AddScoped<ProductQueries>();
builder.Services.AddHealthChecks().AddCheck<CatalogDatabaseHealthCheck>("catalog-database");

var app = builder.Build();
app.Use(async (context, next) =>
{
    var supplied = context.Request.Headers["X-Correlation-ID"].ToString();
    var correlationId = Guid.TryParse(supplied, out var parsed) ? parsed.ToString("D") : Guid.NewGuid().ToString("D");
    context.Request.Headers["X-Correlation-ID"] = correlationId;
    context.Response.Headers["X-Correlation-ID"] = correlationId;
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("CatalogRequests");
    using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId, ["Service"] = "Catalog" }))
    {
        try
        {
            await next(context);
            logger.LogInformation("Catalog {Method} {Path} returned {StatusCode}",
                context.Request.Method, context.Request.Path.Value, context.Response.StatusCode);
        }
        catch (Exception exception) when (exception is DbUpdateException or Npgsql.NpgsqlException)
        {
            logger.LogError(exception, "Catalog database operation failed");
            if (context.Response.HasStarted) throw;
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new { title = "Catalog database unavailable", status = 503 });
        }
    }
});
app.MapHealthChecks("/health");
app.MapGet("/health/live", () => Results.Ok());
app.MapGet("/api/products", (ProductQueries queries, CancellationToken ct) => queries.ListAsync(ct));
app.MapGet("/api/products/{id:guid}", async (Guid id, ProductQueries queries, CancellationToken ct) =>
    await queries.FindAsync(id, ct) is { } product ? Results.Ok(product) : Results.NotFound());
app.MapPost("/api/products", async (ProductInput input, CreateProductHandler handler, IConfiguration config, CancellationToken ct) =>
{
    if (!config.GetValue<bool>("Catalog:WritesEnabled"))
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    if (string.IsNullOrWhiteSpace(input.Name) || input.Price <= 0)
        return Results.BadRequest("Name and positive price are required.");
    var product = await handler.HandleAsync(input.Name, input.Price, ct);
    return Results.Created($"/api/products/{product.Id}", product);
});
app.Run();

public sealed record ProductInput(string Name, decimal Price);
public partial class CatalogProgram;

public sealed class CatalogDatabaseHealthCheck(CatalogDb db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Catalog database is unavailable.");
}
