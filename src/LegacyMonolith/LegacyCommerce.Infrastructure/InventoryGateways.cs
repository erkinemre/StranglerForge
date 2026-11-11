using Polly.CircuitBreaker;
using Polly.Timeout;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

public sealed class LegacySqlInventoryGateway(ICommerceStore store) : IInventoryGateway
{
    public async Task<InventoryStockSnapshot?> GetStockAsync(Guid productId, CancellationToken ct) =>
        await store.GetStockAsync(productId, ct) is { } stock ? new(productId, stock.Quantity) : null;
    public Task<int> AddStockAsync(Guid productId, int quantity, CancellationToken ct) => store.AddStockAsync(productId, quantity, ct);
    public Task<bool> ReserveAsync(Guid reservationId, Guid orderId, Guid productId, int quantity, CancellationToken ct) => store.ReserveStockAsync(productId, quantity, ct);
    public Task ReleaseAsync(Guid reservationId, Guid productId, int quantity, CancellationToken ct) => store.ReleaseStockAsync(productId, quantity, ct);
    public Task ConfirmAsync(Guid reservationId, CancellationToken ct) => Task.CompletedTask;
}

public sealed class InventoryHttpGateway(HttpClient client, ICorrelationIdAccessor correlation, ILogger<InventoryHttpGateway> logger) : IInventoryGateway
{
    public async Task<InventoryStockSnapshot?> GetStockAsync(Guid productId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/inventory/{productId}", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccess(response);
        return await response.Content.ReadFromJsonAsync<InventoryStockSnapshot>(ct) ?? throw new InventoryUnavailableException("Invalid stock response.");
    }
    public async Task<int> AddStockAsync(Guid productId, int quantity, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, $"api/inventory/{productId}/stock", new { quantity }, ct);
        await EnsureSuccess(response);
        return (await response.Content.ReadFromJsonAsync<InventoryStockSnapshot>(ct))?.Quantity ?? throw new InventoryUnavailableException("Invalid stock response.");
    }
    public async Task<bool> ReserveAsync(Guid reservationId, Guid orderId, Guid productId, int quantity, CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Post, "api/inventory/reservations", new { reservationId, orderId, productId, quantity }, ct);
            if (response.StatusCode == HttpStatusCode.Conflict) return false;
            await EnsureSuccess(response);
            return true;
        }
        catch (InventoryUnavailableException exception) when (exception.InnerException is TaskCanceledException or HttpRequestException or TimeoutRejectedException)
        {
            logger.LogWarning(exception, "Reconciling ambiguous reservation {ReservationId}", reservationId);
            using var lookup = await SendAsync(HttpMethod.Get, $"api/inventory/reservations/{reservationId}", null, ct);
            if (lookup.StatusCode == HttpStatusCode.NotFound)
            {
                using var retry = await SendAsync(HttpMethod.Post, "api/inventory/reservations", new { reservationId, orderId, productId, quantity }, ct);
                if (retry.StatusCode == HttpStatusCode.Conflict) return false;
                await EnsureSuccess(retry);
                return true;
            }
            await EnsureSuccess(lookup);
            var reservation = await lookup.Content.ReadFromJsonAsync<ReservationState>(ct);
            return reservation is { Status: 0 } && reservation.OrderId == orderId && reservation.ProductId == productId && reservation.Quantity == quantity;
        }
    }
    public async Task ReleaseAsync(Guid reservationId, Guid productId, int quantity, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, $"api/inventory/reservations/{reservationId}/release", new { }, ct);
        await EnsureSuccess(response);
    }
    public async Task ConfirmAsync(Guid reservationId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, $"api/inventory/reservations/{reservationId}/confirm", new { }, ct);
        await EnsureSuccess(response);
    }
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (!string.IsNullOrWhiteSpace(correlation.CorrelationId)) request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlation.CorrelationId);
        try { return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (Exception exception) when (exception is HttpRequestException or BrokenCircuitException or TimeoutRejectedException or TaskCanceledException && !ct.IsCancellationRequested)
        { throw new InventoryUnavailableException("Inventory service is unavailable.", exception); }
    }
    private static async Task EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw new InventoryUnavailableException($"Inventory service returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }
    private sealed record ReservationState(Guid Id, Guid OrderId, Guid ProductId, int Quantity, int Status);
}
