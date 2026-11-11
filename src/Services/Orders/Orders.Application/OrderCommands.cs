using System.Diagnostics;
using System.Diagnostics.Metrics;
using StranglerForge.Orders.Domain;

namespace StranglerForge.Orders.Application;

public sealed record SubmitOrder(Guid CustomerId, Guid ProductId, int Quantity, Guid? OrderId, Guid CorrelationId);
public sealed record OrderSnapshot(Guid Id, Guid CustomerId, Guid ProductId, int Quantity, decimal UnitPrice, decimal Total, string Status, DateTime CreatedAtUtc);
public sealed record OrderTransitionedV1(Guid EventId, Guid CorrelationId, Guid OrderId, string Status, DateTime OccurredAtUtc, int EventVersion = 1);
public interface IOrderStore
{
    Task<Order?> FindAsync(Guid id, CancellationToken ct);
    Task<OrderSaga?> FindSagaAsync(Guid id, CancellationToken ct);
    Task<bool> TryInsertAsync(Order order, OrderSaga saga, CancellationToken ct);
    Task<bool> TryTransitionAsync(Guid id, OrderStatus expected, OrderStatus next, string? reason, Guid correlationId, CancellationToken ct);
    Task<IReadOnlyList<Guid>> PendingSagaIdsAsync(int limit, CancellationToken ct);
    Task<bool> MarkSagaFlagAsync(Guid id, string flag, CancellationToken ct);
    Task<bool> SetPaymentDecisionAsync(Guid id, bool succeed, CancellationToken ct);
    Task NoteRetryAsync(Guid id, string reason, CancellationToken ct);
}
public interface IOrderDependencies
{
    Task<bool> CustomerExistsAsync(Guid id, CancellationToken ct);
    Task<decimal?> ProductPriceAsync(Guid id, CancellationToken ct);
    Task<ReserveOutcome> ReserveAsync(Guid orderId, Guid productId, int quantity, Guid correlationId, CancellationToken ct);
    Task ReleaseAsync(Guid orderId, Guid correlationId, CancellationToken ct);
    Task ConfirmAsync(Guid orderId, Guid correlationId, CancellationToken ct);
    Task<PaymentOutcome> AuthorizeAsync(Guid orderId, decimal amount, bool succeed, Guid correlationId, CancellationToken ct);
    Task RecordNotificationAsync(Guid orderId, Guid customerId, bool succeeded, Guid correlationId, CancellationToken ct);
}
public enum ReserveOutcome { Reserved, Rejected, Uncertain }
public enum PaymentOutcome { Authorized, Rejected, Uncertain }
public sealed class DependencyUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class OrderCommands(IOrderStore store, IOrderDependencies dependencies)
{
    private static readonly ActivitySource Traces = new("StranglerForge.Orders.Application");
    private static readonly Meter Meter = new("StranglerForge.Orders");
    private static readonly Counter<long> Submitted = Meter.CreateCounter<long>("stranglerforge.orders.submitted");
    public async Task<(OrderSnapshot? Order, string? Error)> SubmitAsync(SubmitOrder command, CancellationToken ct)
    {
        using var activity = Traces.StartActivity("order.submit");
        if (command.CustomerId == Guid.Empty || command.ProductId == Guid.Empty || command.Quantity <= 0 || command.OrderId == Guid.Empty) return (null, "Invalid order.");
        var id = command.OrderId ?? Guid.NewGuid();
        var existing = await store.FindAsync(id, ct);
        if (existing is not null) return existing.Matches(command.CustomerId, command.ProductId, command.Quantity) ? (Snapshot(existing), null) : (null, "Idempotency key belongs to another order.");
        if (!await dependencies.CustomerExistsAsync(command.CustomerId, ct)) return (null, "Customer does not exist.");
        var price = await dependencies.ProductPriceAsync(command.ProductId, ct);
        if (price is null || price <= 0) return (null, "Product does not exist.");
        var now = DateTime.UtcNow;
        var order = Order.Submit(id, command.CustomerId, command.ProductId, command.Quantity, price.Value, now);
        if (!await store.TryInsertAsync(order, OrderSaga.Start(id, command.CorrelationId == Guid.Empty ? id : command.CorrelationId, now), ct))
        {
            existing = await store.FindAsync(id, ct);
            return existing?.Matches(command.CustomerId, command.ProductId, command.Quantity) == true ? (Snapshot(existing), null) : (null, "Idempotency key belongs to another order.");
        }
        Submitted.Add(1);
        return (Snapshot(order), null);
    }
    public async Task<OrderSnapshot?> GetAsync(Guid id, CancellationToken ct) => await store.FindAsync(id, ct) is { } order ? Snapshot(order) : null;
    public static OrderSnapshot Snapshot(Order order) => new(order.Id, order.CustomerId, order.ProductId, order.Quantity, order.UnitPrice, order.Total, order.LegacyStatus ?? order.Status.ToString(), order.CreatedAtUtc);
}
