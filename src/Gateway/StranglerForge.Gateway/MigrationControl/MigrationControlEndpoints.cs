using System.Security.Cryptography;
using System.Text;

namespace StranglerForge.Gateway.MigrationControl;

public static class MigrationControlEndpoints
{
    public static void MapMigrationControl(this WebApplication app)
    {
        var group = app.MapGroup("/internal/migration");
        group.AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            var config = context.RequestServices.GetRequiredService<IConfiguration>();
            var authorization = MigrationAdminAuthorization.Check(context, config);
            if (authorization != StatusCodes.Status200OK) return Results.StatusCode(authorization);
            return await next(invocation);
        });
        group.MapGet("/status", (MigrationStateStore states, IConfiguration config) =>
        {
            try
            {
                var flags = MigrationFlagReader.Read(config);
                return Results.Ok(Enum.GetValues<MigrationService>().Select(service => new { service, state = states.Get(service), flags = flags.For(service), rollback = states.Evaluate(service, flags) }));
            }
            catch (Exception error) when (error is InvalidOperationException or FormatException) { return Results.Problem("Migration configuration is invalid.", statusCode: 503); }
        });
        group.MapGet("/flags", (IConfiguration config) =>
        {
            try { return Results.Ok(MigrationFlagReader.Read(config)); }
            catch (Exception error) when (error is InvalidOperationException or FormatException) { return Results.Problem("Migration configuration is invalid.", statusCode: 503); }
        });
        group.MapGet("/ownership", (MigrationStateStore states, IConfiguration config) =>
        {
            try
            {
                var flags = MigrationFlagReader.Read(config);
                return Results.Ok(Enum.GetValues<MigrationService>().Select(service => new { service, owner = flags.For(service).Owner, state = states.Get(service).Stage, checkpoint = flags.For(service).Checkpoint }));
            }
            catch (Exception error) when (error is InvalidOperationException or FormatException) { return Results.Problem("Migration configuration is invalid.", statusCode: 503); }
        });
        group.MapGet("/rollback/{service}", (MigrationService service, MigrationStateStore states, IConfiguration config) =>
        {
            try { return Results.Ok(states.Evaluate(service, MigrationFlagReader.Read(config))); }
            catch (Exception error) when (error is InvalidOperationException or FormatException) { return Results.Problem("Migration configuration is invalid.", statusCode: 503); }
        });
        group.MapGet("/comparisons", (MigrationDiagnostics diagnostics) => Results.Ok(diagnostics.Snapshot()));
        group.MapGet("/audit", (MigrationStateStore states) => Results.Ok(states.Audit));
        group.MapPost("/{service}/transitions", async (MigrationService service, TransitionRequest request, HttpContext context, MigrationStateStore states, MigrationDiagnostics diagnostics, IConfiguration config, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            MigrationFlags flags;
            try { flags = MigrationFlagReader.Read(config); }
            catch (Exception error) when (error is InvalidOperationException or FormatException) { return Results.Problem("Migration configuration is invalid.", statusCode: 503); }
            if (request.Target == MigrationStage.RollbackPending) diagnostics.Count("rollback.requested");
            var result = await states.TransitionAsync(service, request, flags, "local-admin", context.Request.Headers["X-Correlation-ID"].ToString(), ct);
            if (result.Succeeded) { diagnostics.Count("transition.accepted"); return Results.Ok(result.State); }
            diagnostics.Count("transition.rejected");
            loggerFactory.CreateLogger("MigrationControl").LogWarning("Rejected {Service} migration transition to {Target}: {Reason}", service, request.Target, result.Error);
            return Results.Conflict(new { error = result.Error, state = result.State });
        });
    }
}

public static class MigrationAdminAuthorization
{
    public static int Check(HttpContext context, IConfiguration config)
    {
        var expected = config["MigrationControl:AdminToken"];
        if (string.IsNullOrWhiteSpace(expected)) return StatusCodes.Status503ServiceUnavailable;
        if (context.Connection.RemoteIpAddress is not { } remote || !System.Net.IPAddress.IsLoopback(remote)) return StatusCodes.Status403Forbidden;
        var supplied = context.Request.Headers["X-Migration-Admin-Token"].ToString();
        if (string.IsNullOrWhiteSpace(supplied) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(supplied)))
            return StatusCodes.Status401Unauthorized;
        return StatusCodes.Status200OK;
    }
}
