using Microsoft.AspNetCore.Http;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using StranglerForge.Observability;

namespace StranglerForge.GatewayTests;

public sealed class FaultInjectionTests
{
    [Fact]
    public async Task DevelopmentFaultsRequireTokenAndDoNotAffectLiveness()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Environment.EnvironmentName = "Development";
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FaultInjection:Enabled"] = "true",
            ["FaultInjection:Token"] = "isolated-test-token"
        });
        await using var app = builder.Build();
        app.UseDevelopmentFaultInjection(app.Environment, app.Configuration);
        app.MapGet("/probe", () => Results.Ok());
        app.MapGet("/health/live", () => Results.Ok());
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var unauthorized = new HttpRequestMessage(HttpMethod.Get, "/probe");
        unauthorized.Headers.Add("X-Dev-Fault", "503");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(unauthorized)).StatusCode);
        using var authorized = new HttpRequestMessage(HttpMethod.Get, "/probe");
        authorized.Headers.Add("X-Dev-Fault", "503");
        authorized.Headers.Add("X-Dev-Fault-Token", "isolated-test-token");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.SendAsync(authorized)).StatusCode);
        using var health = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        health.Headers.Add("X-Dev-Fault", "503");
        health.Headers.Add("X-Dev-Fault-Token", "isolated-test-token");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(health)).StatusCode);
    }
}
