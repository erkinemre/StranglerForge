using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace StranglerForge.Observability;

public static class DevelopmentFaultInjection
{
    public static IApplicationBuilder UseDevelopmentFaultInjection(this IApplicationBuilder app, IHostEnvironment environment, IConfiguration configuration)
    {
        if (!environment.IsDevelopment() || !configuration.GetValue<bool>("FaultInjection:Enabled")) return app;
        return app.Use(async (context, next) =>
        {
            var expected = configuration["FaultInjection:Token"];
            var supplied = context.Request.Headers["X-Dev-Fault-Token"].ToString();
            var mode = context.Request.Headers["X-Dev-Fault"].ToString();
            if (context.Request.Path.StartsWithSegments("/health") || string.IsNullOrEmpty(expected) ||
                context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(supplied)))
            {
                await next(context);
                return;
            }
            if (mode == "503") { context.Response.StatusCode = 503; return; }
            if (mode == "slow") await Task.Delay(TimeSpan.FromMilliseconds(500), context.RequestAborted);
            if (mode == "timeout") { await Task.Delay(TimeSpan.FromSeconds(5), context.RequestAborted); context.Response.StatusCode = 503; return; }
            await next(context);
        });
    }
}
