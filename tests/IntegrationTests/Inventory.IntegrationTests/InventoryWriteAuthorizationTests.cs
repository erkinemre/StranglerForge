using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Inventory.IntegrationTests;

public sealed class InventoryWriteAuthorizationTests
{
    [Fact]
    public async Task ProductionWritesRequireSeparateAdminAndReservationTokens()
    {
        await using var factory = new WebApplicationFactory<InventoryProgram>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Production")
                .UseSetting("ConnectionStrings:Inventory", "Host=127.0.0.1;Database=unreachable_auth_test;Username=unused;Password=unused")
                .UseSetting("RabbitMq:UseInMemory", "true")
                .UseSetting("Inventory:StockAdminToken", "stock-test-token")
                .UseSetting("Inventory:ReservationToken", "reservation-test-token"));
        using var client = factory.CreateClient();
        var productId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync($"/api/inventory/{productId}/stock", new { quantity = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/inventory/reservations", new { reservationId, orderId = Guid.NewGuid(), productId, quantity = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync($"/api/inventory/reservations/{reservationId}/release", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync($"/api/inventory/reservations/{reservationId}/confirm", new { })).StatusCode);

        using var authorized = new HttpRequestMessage(HttpMethod.Post, $"/api/inventory/{productId}/stock")
        {
            Content = JsonContent.Create(new { quantity = 0 })
        };
        authorized.Headers.Add("X-Inventory-Admin-Token", "stock-test-token");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(authorized)).StatusCode);
    }
}