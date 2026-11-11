using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using StranglerForge.Observability;

namespace StranglerForge.GatewayTests;

public sealed class TracePropagationTests
{
    [Fact]
    public async Task HttpClientCarriesW3CTraceContextIntoTheReceivingHost()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Telemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddStranglerForgeTelemetry(builder.Configuration, builder.Environment, "trace-test-service");
        await using var app = builder.Build();
        app.MapGet("/trace", () => Results.Ok(Activity.Current?.TraceId.ToString()));
        await app.StartAsync();
        using var activity = Telemetry.Source.StartActivity("test.root");
        Assert.NotNull(activity);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var body = await client.GetStringAsync("/trace");
        Assert.Contains(activity.TraceId.ToString(), body);
    }
}
