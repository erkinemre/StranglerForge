using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LegacyCommerce.IntegrationTests;

public sealed class CatalogBoundaryTests
{
    [Fact]
    public async Task ExtractedModeDisablesLegacyCatalogAndDoesNotFallBackOnCatalogOutage()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("InventoryOwnership:Mode", "Legacy")
                .UseSetting("OrdersOwnership:Mode", "Legacy")
                .UseSetting("CatalogOwnership:Mode", "Extracted")
                .UseSetting("CatalogService:BaseUrl", "http://127.0.0.1:1/"));
        using var client = factory.CreateClient();
        var productId = Guid.Parse("00000000-0000-0000-0000-000000000201");
        var customerId = Guid.Parse("00000000-0000-0000-0000-000000000101");
        Assert.Equal(HttpStatusCode.Gone, (await client.GetAsync("/api/products")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await client.PostAsJsonAsync("/api/products", new { name = "Blocked", price = 1m })).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await client.PostAsJsonAsync($"/api/inventory/{productId}/stock", new { quantity = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await client.PostAsJsonAsync("/api/orders", new { customerId, productId, quantity = 1 })).StatusCode);
    }
}
