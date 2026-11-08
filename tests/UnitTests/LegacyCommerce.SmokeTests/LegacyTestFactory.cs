using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace LegacyCommerce.IntegrationTests;

public static class LegacyTestFactory
{
    public static WebApplicationFactory<Program> Create() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("CatalogOwnership:Mode", "Legacy")
                .UseSetting("InventoryOwnership:Mode", "Legacy")
                .UseSetting("OrdersOwnership:Mode", "Legacy")
                .UseSetting("ConnectionStrings:LegacyCommerce", "Server=localhost;Database=StranglerForgeLegacyCommerceTests;Trusted_Connection=True;TrustServerCertificate=True")
                .ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CatalogOwnership:Mode"] = "Legacy",
                    ["InventoryOwnership:Mode"] = "Legacy",
                    ["OrdersOwnership:Mode"] = "Legacy",
                    ["ConnectionStrings:LegacyCommerce"] = "Server=localhost;Database=StranglerForgeLegacyCommerceTests;Trusted_Connection=True;TrustServerCertificate=True"
                })));
}
