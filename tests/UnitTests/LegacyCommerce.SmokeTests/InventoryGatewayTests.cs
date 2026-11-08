using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace LegacyCommerce.IntegrationTests;

public sealed class InventoryGatewayTests
{
    [Fact]
    public async Task TimeoutReconcilesUsingTheSameReservationIdentifier()
    {
        var reservationId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var posts = 0;
        var gets = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Post) { posts++; throw new TaskCanceledException("Simulated ambiguous timeout"); }
            gets++;
            Assert.Equal($"/api/inventory/reservations/{reservationId}", request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { id = reservationId, orderId, productId, quantity = 2, status = 0 })
            };
        })) { BaseAddress = new Uri("http://inventory.test/") };
        var gateway = new InventoryHttpGateway(client, new CorrelationIdAccessor { CorrelationId = orderId.ToString() }, NullLogger<InventoryHttpGateway>.Instance);
        Assert.True(await gateway.ReserveAsync(reservationId, orderId, productId, 2, CancellationToken.None));
        Assert.Equal(1, posts);
        Assert.Equal(1, gets);
    }

    [Fact]
    public async Task MissingStatusRetriesOnlyWithTheOriginalIdentifier()
    {
        var reservationId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var postedBodies = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.NotFound);
            postedBodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            if (postedBodies.Count == 1) throw new TaskCanceledException("Simulated ambiguous timeout");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { id = reservationId }) };
        })) { BaseAddress = new Uri("http://inventory.test/") };
        var gateway = new InventoryHttpGateway(client, new CorrelationIdAccessor(), NullLogger<InventoryHttpGateway>.Instance);
        Assert.True(await gateway.ReserveAsync(reservationId, orderId, productId, 1, CancellationToken.None));
        Assert.Equal(2, postedBodies.Count);
        Assert.Equal(postedBodies[0], postedBodies[1]);
        Assert.Contains(reservationId.ToString(), postedBodies[0], StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(reply(request));
    }
}
