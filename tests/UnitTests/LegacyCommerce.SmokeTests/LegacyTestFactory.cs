using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace LegacyCommerce.IntegrationTests;

public static class LegacyTestFactory
{
    private static string Connection => Environment.GetEnvironmentVariable("LEGACY_TEST_CONNECTION")
        ?? "Server=localhost;Database=StranglerForgeLegacyCommerceTests;Trusted_Connection=True;TrustServerCertificate=True";

    public static WebApplicationFactory<Program> Create() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("CatalogOwnership:Mode", "Legacy")
                .UseSetting("InventoryOwnership:Mode", "Legacy")
                .UseSetting("OrdersOwnership:Mode", "Legacy")
                .UseSetting("ConnectionStrings:LegacyCommerce", Connection)
                .ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CatalogOwnership:Mode"] = "Legacy",
                    ["InventoryOwnership:Mode"] = "Legacy",
                    ["OrdersOwnership:Mode"] = "Legacy",
                    ["ConnectionStrings:LegacyCommerce"] = Connection
                })));
}
