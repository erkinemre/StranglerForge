namespace StranglerForge.Inventory.Application;

public interface IInventoryProductCatalog
{
    Task<bool> ExistsAsync(Guid productId, CancellationToken ct);
}
public sealed class InventoryCatalogUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
