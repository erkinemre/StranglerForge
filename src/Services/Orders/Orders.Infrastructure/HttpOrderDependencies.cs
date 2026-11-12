using Polly.CircuitBreaker;
using Polly.Timeout;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Http.Json;
using StranglerForge.Orders.Application;

namespace StranglerForge.Orders.Infrastructure;

public sealed class HttpOrderDependencies(IHttpClientFactory factory, IConfiguration configuration) : IOrderDependencies
{
    private readonly Uri legacy = new(configuration["LegacyService:BaseUrl"] ?? throw new InvalidOperationException("LegacyService:BaseUrl required."));
    private readonly Uri catalog = new(configuration["CatalogService:BaseUrl"] ?? throw new InvalidOperationException("CatalogService:BaseUrl required."));
    private readonly Uri inventory = new(configuration["InventoryService:BaseUrl"] ?? throw new InvalidOperationException("InventoryService:BaseUrl required."));
    private readonly string token = configuration["OrdersInternal:Token"] ?? throw new InvalidOperationException("OrdersInternal:Token required.");
    public async Task<bool> CustomerExistsAsync(Guid id, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, "orders-legacy", legacy, $"api/customers/{id}", null, Guid.NewGuid(), false, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        Ensure(response); return true;
    }
    public async Task<decimal?> ProductPriceAsync(Guid id, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, "orders-catalog", catalog, $"api/products/{id}", null, Guid.NewGuid(), false, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        Ensure(response);
        var product = await response.Content.ReadFromJsonAsync<ProductResponse>(cancellationToken: ct);
        return product?.Price;
    }
    public async Task<ReserveOutcome> ReserveAsync(Guid orderId, Guid productId, int quantity, Guid correlationId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, "orders-inventory", inventory, "api/inventory/reservations", new { reservationId = orderId, orderId, productId, quantity }, correlationId, false, ct);
        if (response.IsSuccessStatusCode) return ReserveOutcome.Reserved;
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            return body.Contains("Insufficient stock", StringComparison.OrdinalIgnoreCase) ? ReserveOutcome.Rejected : ReserveOutcome.Uncertain;
        }
        return ReserveOutcome.Uncertain;
    }
    public async Task ReleaseAsync(Guid orderId, Guid correlationId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, "orders-inventory", inventory, $"api/inventory/reservations/{orderId}/release", new { }, correlationId, false, ct);
        Ensure(response);
    }
    public async Task ConfirmAsync(Guid orderId, Guid correlationId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, "orders-inventory", inventory, $"api/inventory/reservations/{orderId}/confirm", new { }, correlationId, false, ct);
        Ensure(response);
    }
    public async Task<PaymentOutcome> AuthorizeAsync(Guid orderId, decimal amount, bool succeed, Guid correlationId, CancellationToken ct)
    {
        HttpResponseMessage response;
        try { response = await SendAsync(HttpMethod.Post, "orders-legacy", legacy, $"internal/payments/{orderId}/authorize", new { amount, succeed }, correlationId, true, ct); }
        catch (DependencyUnavailableException) { return PaymentOutcome.Uncertain; }
        using var ownedResponse = response;
        if (response.StatusCode == HttpStatusCode.Conflict) return PaymentOutcome.Uncertain;
        if (!response.IsSuccessStatusCode) return PaymentOutcome.Uncertain;
        var result = await response.Content.ReadFromJsonAsync<PaymentResponse>(cancellationToken: ct);
        return result?.Succeeded == true ? PaymentOutcome.Authorized : PaymentOutcome.Rejected;
    }
    public async Task RecordNotificationAsync(Guid orderId, Guid customerId, bool succeeded, Guid correlationId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, "orders-legacy", legacy, $"internal/notifications/{orderId}", new { customerId, succeeded }, correlationId, true, ct);
        Ensure(response);
    }
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string dependency, Uri root, string path, object? body, Guid correlationId, bool internalCall, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, new Uri(root, path));
            request.Headers.Add("X-Correlation-ID", correlationId.ToString("D"));
            if (internalCall) request.Headers.Add("X-Orders-Internal-Token", token);
            if (dependency == "orders-inventory" && method != HttpMethod.Get && configuration["Inventory:ReservationToken"] is { Length: > 0 } inventoryToken)
                request.Headers.Add("X-Inventory-Internal-Token", inventoryToken);
            if (body is not null) request.Content = JsonContent.Create(body);
            return await factory.CreateClient(dependency).SendAsync(request, ct);
        }
        catch (HttpRequestException error) { throw new DependencyUnavailableException($"{root.Host} unavailable.", error); }
        catch (TaskCanceledException error) when (!ct.IsCancellationRequested) { throw new DependencyUnavailableException($"{root.Host} timed out.", error); }
        catch (Exception error) when (!ct.IsCancellationRequested && error is BrokenCircuitException or TimeoutRejectedException)
        { throw new DependencyUnavailableException($"{root.Host} resilience policy rejected the call.", error); }
    }
    private static void Ensure(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw new DependencyUnavailableException($"Dependency returned {(int)response.StatusCode}.");
    }
    private sealed record ProductResponse(Guid Id, decimal Price);
    private sealed record PaymentResponse(bool Succeeded);
}
