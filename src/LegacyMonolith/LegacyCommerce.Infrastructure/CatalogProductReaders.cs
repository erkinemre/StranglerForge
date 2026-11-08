using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

public sealed class CatalogHttpProductReader(
    HttpClient client,
    ICorrelationIdAccessor correlation,
    ILogger<CatalogHttpProductReader> logger) : IProductCatalog
{
    public async Task<ProductSnapshot?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/products/{id}");
        if (!string.IsNullOrWhiteSpace(correlation.CorrelationId))
            request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlation.CorrelationId);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode)
                throw new CatalogUnavailableException($"Catalog lookup returned {(int)response.StatusCode}.");
            var product = await response.Content.ReadFromJsonAsync<CatalogProductResponse>(cancellationToken);
            return product is null || product.Id != id || product.Price <= 0 ? throw new CatalogUnavailableException("Catalog returned an invalid product.") :
                new ProductSnapshot(product.Id, product.Price);
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException ||
            exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Catalog lookup failed for product {ProductId}", id);
            throw new CatalogUnavailableException("Catalog lookup is unavailable.", exception);
        }
    }

    private sealed record CatalogProductResponse(Guid Id, string Name, decimal Price);
}

public sealed class LegacySqlProductReader(CommerceDb db) : IProductCatalog
{
    public async Task<ProductSnapshot?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var product = await db.GetProductAsync(id, cancellationToken);
        return product is null ? null : new ProductSnapshot(product.Id, product.Price);
    }
}
