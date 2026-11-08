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
    public Task<StockAvailability?> GetStockAsync(Guid productId, CancellationToken ct) => store.GetStockAsync(productId, ct);
    public Task<ReservationSnapshot?> GetReservationAsync(Guid id, CancellationToken ct) => store.GetReservationAsync(id, ct);
    public Task<InventoryResult<ReservationSnapshot>> ReserveAsync(ReserveStock command, CancellationToken ct) => store.ReserveAsync(command, ct);
    public Task<InventoryResult<ReservationSnapshot>> ReleaseAsync(Guid id, CancellationToken ct) => store.ReleaseAsync(id, ct);
    public Task<InventoryResult<ReservationSnapshot>> ConfirmAsync(Guid id, CancellationToken ct) => store.ConfirmAsync(id, ct);
    public async Task<InventoryResult<StockAvailability>> AdjustAsync(Guid productId, int delta, string reason, CancellationToken ct)
    {
        if (!await catalog.ExistsAsync(productId, ct)) return InventoryResult<StockAvailability>.Fail("Product does not exist.");
        return await store.AdjustAsync(productId, delta, reason, ct);
    }
}
