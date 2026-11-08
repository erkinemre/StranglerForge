public sealed record InventoryStockSnapshot(Guid ProductId, int Quantity);
public interface IInventoryGateway
{
    Task<InventoryStockSnapshot?> GetStockAsync(Guid productId, CancellationToken ct);
    Task<int> AddStockAsync(Guid productId, int quantity, CancellationToken ct);
    Task<bool> ReserveAsync(Guid reservationId, Guid orderId, Guid productId, int quantity, CancellationToken ct);
    Task ReleaseAsync(Guid reservationId, Guid productId, int quantity, CancellationToken ct);
    Task ConfirmAsync(Guid reservationId, CancellationToken ct);
}
public sealed class InventoryUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
