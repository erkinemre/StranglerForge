using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using StranglerForge.Orders.Domain;

namespace StranglerForge.Orders.Application;

public sealed class OrderSagaRunner(IOrderStore store, IOrderDependencies dependencies, ILogger<OrderSagaRunner> logger)
{
    private static readonly ActivitySource Traces = new("StranglerForge.Orders.Application");
    private static readonly Meter Meter = new("StranglerForge.Orders");
    private static readonly Histogram<double> SagaDuration = Meter.CreateHistogram<double>("stranglerforge.orders.saga_step.duration", "s");
    private static readonly Counter<long> CompensationAttempts = Meter.CreateCounter<long>("stranglerforge.orders.compensation.attempts");
    public async Task RunPendingAsync(CancellationToken ct)
    {
        foreach (var id in await store.PendingSagaIdsAsync(25, ct))
        {
            try { await AdvanceAsync(id, ct); }
            catch (DependencyUnavailableException error) { await store.NoteRetryAsync(id, "Transient dependency unavailable", ct); logger.LogWarning(error, "Order {OrderId} dependency unavailable; saga state retained for retry", id); }
        }
    }
    public async Task AdvanceAsync(Guid id, CancellationToken ct)
    {
        using var activity = Traces.StartActivity("order.saga.advance");
        var started = Stopwatch.GetTimestamp();
        try { await AdvanceCoreAsync(id, ct); }
        finally { SagaDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds); }
    }
    private async Task AdvanceCoreAsync(Guid id, CancellationToken ct)
    {
        var order = await store.FindAsync(id, ct);
        var saga = await store.FindSagaAsync(id, ct);
        if (order is null || saga is null || order.Status != saga.State) return;
        var correlation = saga.CorrelationId;
        switch (saga.State)
        {
            case OrderStatus.Submitted:
                await store.TryTransitionAsync(id, OrderStatus.Submitted, OrderStatus.InventoryPending, null, correlation, ct);
                break;
            case OrderStatus.InventoryPending:
                var reservation = await dependencies.ReserveAsync(id, order.ProductId, order.Quantity, correlation, ct);
                if (reservation == ReserveOutcome.Reserved)
                {
                    await store.MarkSagaFlagAsync(id, "reserved", ct);
                    await store.TryTransitionAsync(id, OrderStatus.InventoryPending, OrderStatus.InventoryReserved, null, correlation, ct);
                }
                else if (reservation == ReserveOutcome.Rejected)
                    await store.TryTransitionAsync(id, OrderStatus.InventoryPending, OrderStatus.Failed, "Insufficient inventory", correlation, ct);
                break;
            case OrderStatus.InventoryReserved:
                await store.TryTransitionAsync(id, OrderStatus.InventoryReserved, OrderStatus.PaymentPending, null, correlation, ct);
                break;
            case OrderStatus.PaymentPending:
                if (saga.PaymentDecision is null || saga.LastError == "Payment outcome uncertain; manual review required") break;
                var outcome = await dependencies.AuthorizeAsync(id, order.Total, saga.PaymentDecision.Value, correlation, ct);
                if (outcome == PaymentOutcome.Authorized)
                    await store.TryTransitionAsync(id, OrderStatus.PaymentPending, OrderStatus.PaymentAuthorized, null, correlation, ct);
                else if (outcome == PaymentOutcome.Rejected)
                    await store.TryTransitionAsync(id, OrderStatus.PaymentPending, OrderStatus.CompensationPending, "Payment rejected", correlation, ct);
                else
                    await store.TryTransitionAsync(id, OrderStatus.PaymentPending, OrderStatus.PaymentPending, "Payment outcome uncertain; manual review required", correlation, ct);
                break;
            case OrderStatus.PaymentAuthorized:
                await dependencies.ConfirmAsync(id, correlation, ct);
                await store.TryTransitionAsync(id, OrderStatus.PaymentAuthorized, OrderStatus.Confirmed, null, correlation, ct);
                break;
            case OrderStatus.CompensationPending:
                CompensationAttempts.Add(1);
                await dependencies.ReleaseAsync(id, correlation, ct);
                await store.MarkSagaFlagAsync(id, "released", ct);
                await store.TryTransitionAsync(id, OrderStatus.CompensationPending, OrderStatus.Cancelled, saga.LastError, correlation, ct);
                break;
        }
        var current = await store.FindSagaAsync(id, ct);
        if (current is { State: OrderStatus.Confirmed or OrderStatus.Cancelled or OrderStatus.Failed, NotificationRecorded: false })
        {
            await dependencies.RecordNotificationAsync(id, order.CustomerId, current.State == OrderStatus.Confirmed, correlation, ct);
            await store.MarkSagaFlagAsync(id, "notification", ct);
        }
    }
}
