using Polly.CircuitBreaker;
using Polly.Timeout;
using System.Net;
using Microsoft.Extensions.Logging;
using StranglerForge.Inventory.Application;

namespace StranglerForge.Inventory.Infrastructure;

public sealed class InventoryCatalogHttpLookup(HttpClient client, IInventoryCorrelation correlation, ILogger<InventoryCatalogHttpLookup> logger) : IInventoryProductCatalog
{
    public async Task<bool> ExistsAsync(Guid productId, CancellationToken ct)
    {
        if (productId == Guid.Empty) return false;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/products/{productId}");
        if (correlation.CorrelationId != Guid.Empty) request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlation.CorrelationId.ToString("D"));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return false;
            if (!response.IsSuccessStatusCode) throw new InventoryCatalogUnavailableException($"Catalog returned {(int)response.StatusCode}.");
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or BrokenCircuitException or TimeoutRejectedException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogError(exception, "Catalog product check failed for {ProductId}", productId);
            throw new InventoryCatalogUnavailableException("Catalog product check is unavailable.", exception);
        }
    }
}
