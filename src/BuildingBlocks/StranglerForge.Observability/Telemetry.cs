using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace StranglerForge.Observability;

public static class Telemetry
{
    public const string SourceName = "StranglerForge.Application";
    public const string MeterName = "StranglerForge.Operations";
    public static readonly ActivitySource Source = new(SourceName);
    public static readonly Meter Meter = new(MeterName);
    public static readonly Counter<long> ConsumerProcessed = Meter.CreateCounter<long>("stranglerforge.consumer.processed");
    public static readonly Counter<long> ConsumerFailures = Meter.CreateCounter<long>("stranglerforge.consumer.failures");
    public static readonly Counter<long> DependencyFailures = Meter.CreateCounter<long>("stranglerforge.dependency.failures");

    public static IServiceCollection AddStranglerForgeTelemetry(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment, string serviceName)
    {
        services.Configure<LoggerFactoryOptions>(options => options.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);
        var instanceId = Environment.GetEnvironmentVariable("OTEL_SERVICE_INSTANCE_ID") ?? Guid.NewGuid().ToString("N");
        var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "1.0.0";
        var endpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var telemetry = services.AddOpenTelemetry()
            .ConfigureResource(builder => builder.AddService(serviceName, serviceVersion: version, serviceInstanceId: instanceId)
                .AddAttributes(new Dictionary<string, object> { ["deployment.environment"] = environment.EnvironmentName }));
        telemetry.WithTracing(builder =>
        {
            builder.AddSource(SourceName, "StranglerForge.Orders.Application", "StranglerForge.Inventory.Application", "MassTransit", "Npgsql").AddAspNetCoreInstrumentation(options =>
            {
                options.Filter = context => !context.Request.Path.StartsWithSegments("/health");
                options.RecordException = true;
            }).AddHttpClientInstrumentation();
            if (Uri.TryCreate(endpoint, UriKind.Absolute, out _)) builder.AddOtlpExporter();
        });
        telemetry.WithMetrics(builder =>
        {
            builder.AddMeter(MeterName, "StranglerForge.Outbox", "StranglerForge.Migration", "StranglerForge.Orders", "StranglerForge.Inventory", "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel", "System.Net.Http", "Polly");
            if (Uri.TryCreate(endpoint, UriKind.Absolute, out _)) builder.AddOtlpExporter();
        });
        return services;
    }
}
