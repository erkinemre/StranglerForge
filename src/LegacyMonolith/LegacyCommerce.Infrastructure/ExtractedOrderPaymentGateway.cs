using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;

public sealed class ExtractedOrderPaymentGateway(HttpClient client, IConfiguration config) : IExtractedOrderPayment
{
    public async Task<(int StatusCode, string? Body)> SubmitIntentAsync(Guid orderId, bool succeed, string? correlationId, CancellationToken ct)
    {
        var token = config["OrdersInternal:Token"] ?? throw new InvalidOperationException("OrdersInternal:Token required in extracted order mode.");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"internal/orders/{orderId}/payment-intent") { Content = JsonContent.Create(new { succeed }) };
        request.Headers.Add("X-Orders-Internal-Token", token);
        if (Guid.TryParse(correlationId, out var parsed)) request.Headers.Add("X-Correlation-ID", parsed.ToString("D"));
        try
        {
            using var response = await client.SendAsync(request, ct);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        }
        catch (HttpRequestException) { return (503, null); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return (503, null); }
    }
}
