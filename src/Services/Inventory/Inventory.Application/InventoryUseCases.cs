using System.Diagnostics;
using System.Diagnostics.Metrics;
namespace StranglerForge.Inventory.Application;

using StranglerForge.Inventory.Domain;

public sealed record InventoryResult<T>(T? Value, string? Error = null)
{
    public bool Succeeded => Error is null;
    public static InventoryResult<T> Ok(T value) => new(value);
    public static InventoryResult<T> Fail(string error) => new(default, error);
}
public sealed record StockAvailability(Guid ProductId, int Quantity, int Reserved, int Available);
public sealed record ReservationSnapshot(Guid Id, Guid OrderId, Guid ProductId, int Quantity, ReservationStatus Status);
public sealed record ReserveStock(Guid ReservationId, Guid OrderId, Guid ProductId, int Quantity);

public interface IInventoryStore
{
    Task<StockAvailability?> GetStockAsync(Guid productId, CancellationToken ct);
    Task<ReservationSnapshot?> GetReservationAsync(Guid id, CancellationToken ct);
    Task<InventoryResult<ReservationSnapshot>> ReserveAsync(ReserveStock command, CancellationToken ct);
    Task<InventoryResult<ReservationSnapshot>> ReleaseAsync(Guid id, CancellationToken ct);
    Task<InventoryResult<ReservationSnapshot>> ConfirmAsync(Guid id, CancellationToken ct);
    Task<InventoryResult<StockAvailability>> AdjustAsync(Guid productId, int delta, string reason, CancellationToken ct);
}

public sealed class InventoryUseCases(IInventoryStore store, IInventoryProductCatalog catalog)
{
    private static readonly ActivitySource Traces = new("StranglerForge.Inventory.Application");
    private static readonly Meter Meter = new("StranglerForge.Inventory");
    private static readonly Counter<long> Reservations = Meter.CreateCounter<long>("stranglerforge.inventory.reservations");
    private static readonly Histogram<double> ReservationDuration = Meter.CreateHistogram<double>("stranglerforge.inventory.reservation.duration", "s");
    public Task<StockAvailability?> GetStockAsync(Guid productId, CancellationToken ct) => store.GetStockAsync(productId, ct);
    public Task<ReservationSnapshot?> GetReservationAsync(Guid id, CancellationToken ct) => store.GetReservationAsync(id, ct);
    public async Task<InventoryResult<ReservationSnapshot>> ReserveAsync(ReserveStock command, CancellationToken ct)
    {
        using var activity = Traces.StartActivity("inventory.reserve");
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await store.ReserveAsync(command, ct);
            Reservations.Add(1, new KeyValuePair<string, object?>("outcome", result.Succeeded ? "success" : "rejected"));
            return result;
        }
        finally { ReservationDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds); }
    }
    public Task<InventoryResult<ReservationSnapshot>> ReleaseAsync(Guid id, CancellationToken ct) => store.ReleaseAsync(id, ct);
    public Task<InventoryResult<ReservationSnapshot>> ConfirmAsync(Guid id, CancellationToken ct) => store.ConfirmAsync(id, ct);
    public async Task<InventoryResult<StockAvailability>> AdjustAsync(Guid productId, int delta, string reason, CancellationToken ct)
    {
        if (!await catalog.ExistsAsync(productId, ct)) return InventoryResult<StockAvailability>.Fail("Product does not exist.");
        return await store.AdjustAsync(productId, delta, reason, ct);
    }
}
