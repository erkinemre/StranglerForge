using Yarp.ReverseProxy.Forwarder;
var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole();
builder.Services.AddHealthChecks();
builder.Services.AddRequestTimeouts();
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
app.UseRouting();
app.UseRequestTimeouts();
app.MapHealthChecks("/health");
app.MapReverseProxy();
app.Run();

public partial class GatewayProgram;
