using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using StranglerForge.Orders.Domain;
using StranglerForge.Orders.Infrastructure;

public sealed class SagaMetricsWorker(IServiceScopeFactory scopes, ILogger<SagaMetricsWorker> logger) : BackgroundService
{
    private static readonly Meter Meter = new("StranglerForge.Orders");
    private long active;
    private long pendingPayment;
    private long sampledAtTicks;
    private ObservableGauge<long>? activeGauge;
    private ObservableGauge<long>? paymentGauge;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        activeGauge = Meter.CreateObservableGauge("stranglerforge.orders.sagas.active", () => Observe(Interlocked.Read(ref active)));
        paymentGauge = Meter.CreateObservableGauge("stranglerforge.orders.payments.pending", () => Observe(Interlocked.Read(ref pendingPayment)));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OrdersDb>();
                var count = await db.Sagas.CountAsync(x => x.State != OrderStatus.Confirmed && x.State != OrderStatus.Cancelled && x.State != OrderStatus.Failed, stoppingToken);
                var payment = await db.Sagas.CountAsync(x => x.State == OrderStatus.PaymentPending, stoppingToken);
                Interlocked.Exchange(ref active, count);
                Interlocked.Exchange(ref pendingPayment, payment);
                Interlocked.Exchange(ref sampledAtTicks, DateTime.UtcNow.Ticks);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger.LogWarning(error, "Saga metrics probe failed"); }
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    private IEnumerable<Measurement<long>> Observe(long value) =>
        DateTime.UtcNow.Ticks - Interlocked.Read(ref sampledAtTicks) < TimeSpan.FromSeconds(30).Ticks ? [new(value)] : [];
}
