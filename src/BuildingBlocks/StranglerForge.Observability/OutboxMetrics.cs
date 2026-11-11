using System.Diagnostics.Metrics;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace StranglerForge.Observability;

public sealed class OutboxMetricsWorker<TDb>(IServiceScopeFactory scopes, ILogger<OutboxMetricsWorker<TDb>> logger, string service) : BackgroundService where TDb : DbContext
{
    private static readonly Meter Meter = new("StranglerForge.Outbox");
    private long pending;
    private long oldestTicks;
    private long sampledAtTicks;
    private readonly KeyValuePair<string, object?> serviceTag = new("service", service);
    private ObservableGauge<long>? pendingGauge;
    private ObservableGauge<double>? ageGauge;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        pendingGauge = Meter.CreateObservableGauge("stranglerforge.outbox.pending", ObservePending);
        ageGauge = Meter.CreateObservableGauge("stranglerforge.outbox.oldest.age", ObserveAge, "s");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TDb>();
                var messages = db.Set<OutboxMessage>().AsNoTracking();
                var count = await messages.CountAsync(stoppingToken);
                DateTime? oldest = count == 0 ? null : await messages.OrderBy(x => x.EnqueueTime).Select(x => x.EnqueueTime).FirstAsync(stoppingToken);
                Interlocked.Exchange(ref pending, count);
                Interlocked.Exchange(ref oldestTicks, oldest?.ToUniversalTime().Ticks ?? 0);
                Interlocked.Exchange(ref sampledAtTicks, DateTime.UtcNow.Ticks);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger.LogWarning(error, "Outbox metrics probe failed for {Service}", service); }
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    private bool Fresh => DateTime.UtcNow.Ticks - Interlocked.Read(ref sampledAtTicks) < TimeSpan.FromSeconds(30).Ticks;
    private IEnumerable<Measurement<long>> ObservePending() => Fresh ? [new(Interlocked.Read(ref pending), serviceTag)] : [];
    private IEnumerable<Measurement<double>> ObserveAge() => Fresh
        ? [new(Interlocked.Read(ref oldestTicks) == 0 ? 0 : Math.Max(0, (DateTime.UtcNow - new DateTime(Interlocked.Read(ref oldestTicks), DateTimeKind.Utc)).TotalSeconds), serviceTag)] : [];
}

public static class OutboxMetricsRegistration
{
    public static IServiceCollection AddOutboxMetrics<TDb>(this IServiceCollection services, string service) where TDb : DbContext
    {
        services.AddSingleton<IHostedService>(provider => new OutboxMetricsWorker<TDb>(provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<ILogger<OutboxMetricsWorker<TDb>>>(), service));
        return services;
    }
}
