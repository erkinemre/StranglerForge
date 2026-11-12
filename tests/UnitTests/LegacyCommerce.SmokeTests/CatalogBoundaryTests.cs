using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LegacyCommerce.IntegrationTests;

public sealed class CatalogBoundaryTests
{
    [Fact]
    public async Task ExtractedModeDisablesLegacyCatalogAndDoesNotFallBackOnCatalogOutage()
    {
        var connection = Environment.GetEnvironmentVariable("LEGACY_TEST_CONNECTION")
            ?? "Server=localhost;Database=StranglerForgeLegacyCommerceTests;Trusted_Connection=True;TrustServerCertificate=True";
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:LegacyCommerce", connection)
                .UseSetting("InventoryOwnership:Mode", "Legacy")
                .UseSetting("OrdersOwnership:Mode", "Legacy")
                .UseSetting("CatalogOwnership:Mode", "Extracted")
                .UseSetting("CatalogService:BaseUrl", "http://127.0.0.1:1/"));
        using var client = factory.CreateClient();
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<CommerceDb>().Database.MigrateAsync();
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
