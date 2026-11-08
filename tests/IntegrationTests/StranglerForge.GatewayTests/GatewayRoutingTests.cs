using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace StranglerForge.GatewayTests;

public sealed class GatewayRoutingTests
{
    [Theory]
    [InlineData("POST", "/api/customers")]
    [InlineData("GET", "/api/customers/00000000-0000-0000-0000-000000000101")]
    [InlineData("GET", "/api/products")]
    [InlineData("POST", "/api/products")]
    [InlineData("GET", "/api/inventory/00000000-0000-0000-0000-000000000201")]
    [InlineData("POST", "/api/inventory/00000000-0000-0000-0000-000000000201/stock")]
    [InlineData("POST", "/api/orders")]
    [InlineData("GET", "/api/orders/00000000-0000-0000-0000-000000000301")]
    [InlineData("POST", "/api/orders/00000000-0000-0000-0000-000000000301/payment")]
    [InlineData("GET", "/api/notifications/00000000-0000-0000-0000-000000000101")]
    [InlineData("POST", "/api/notifications/00000000-0000-0000-0000-000000000401/dispatch")]
    public async Task ForwardsExistingRoutes(string method, string path)
    {
        await using var legacy = await StartLegacyAsync();
        await using var gateway = CreateGateway(legacy.Urls.Single());
        using var client = gateway.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path + "?source=gateway");
        if (method == "POST") request.Content = JsonContent.Create(new { marker = "body-preserved" });
        var correlationId = Guid.NewGuid().ToString("D");
        request.Headers.Add("X-Correlation-ID", correlationId);
        using var response = await client.SendAsync(request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        Assert.Equal("legacy", response.Headers.GetValues("X-Legacy").Single());
        Assert.Equal(correlationId, response.Headers.GetValues("X-Correlation-ID").Single());
        using var echo = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(method, echo.RootElement.GetProperty("method").GetString());
        Assert.Equal(path, echo.RootElement.GetProperty("path").GetString());
        Assert.Equal("?source=gateway", echo.RootElement.GetProperty("query").GetString());
        Assert.Equal(correlationId, echo.RootElement.GetProperty("correlationId").GetString());
        if (method == "POST")
            Assert.Contains("body-preserved", echo.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task HealthIsGatewayOwnedAndUnknownRoutesAreNotForwarded()
    {
        await using var gateway = CreateGateway("http://127.0.0.1:1/");
        using var client = gateway.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/not-a-business-route")).StatusCode);
    }

    [Fact]
    public async Task ReplacesUntrustedForwardedFor()
    {
        await using var legacy = await StartLegacyAsync();
        await using var gateway = CreateGateway(legacy.Urls.Single());
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/products");
        request.Headers.Add("X-Forwarded-For", "198.51.100.1");
        using var response = await gateway.CreateClient().SendAsync(request);
        using var echo = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("198.51.100.1", echo.RootElement.GetProperty("forwardedFor").GetString());
    }
    [Fact]
    public async Task PreservesLegacyStatusCode()
    {
        await using var legacy = await StartLegacyAsync();
        await using var gateway = CreateGateway(legacy.Urls.Single());
        using var response = await gateway.CreateClient().GetAsync("/api/products?status=418");
        Assert.Equal((HttpStatusCode)418, response.StatusCode);
    }

    [Fact]
    public async Task UnavailableLegacyReturnsGatewayErrorWhileHealthStaysHealthy()
    {
        await using var gateway = CreateGateway("http://127.0.0.1:1/");
        using var client = gateway.CreateClient();
        var response = await client.GetAsync("/api/products");
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.BadGateway, HttpStatusCode.ServiceUnavailable, HttpStatusCode.GatewayTimeout });
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task CatalogRoutesUseExtractedServiceWhileOtherRoutesUseLegacy()
    {
        await using var legacy = await StartLegacyAsync("legacy");
        await using var catalog = await StartLegacyAsync("catalog");
        await using var gateway = CreateGateway(legacy.Urls.Single(), catalog.Urls.Single());
        using var client = gateway.CreateClient();
        using var catalogResponse = await client.GetAsync("/api/products");
        using var detailResponse = await client.GetAsync("/api/products/00000000-0000-0000-0000-000000000201");
        using var customerResponse = await client.GetAsync("/api/customers/00000000-0000-0000-0000-000000000101");
        Assert.Equal("catalog", catalogResponse.Headers.GetValues("X-Legacy").Single());
        Assert.Equal("catalog", detailResponse.Headers.GetValues("X-Legacy").Single());
        Assert.Equal("legacy", customerResponse.Headers.GetValues("X-Legacy").Single());
    }

    [Fact]
    public async Task CatalogOutageDoesNotBreakLegacyRouting()
    {
        await using var legacy = await StartLegacyAsync();
        await using var gateway = CreateGateway(legacy.Urls.Single(), "http://127.0.0.1:1/");
        using var client = gateway.CreateClient();
        using var catalogResponse = await client.GetAsync("/api/products");
        using var customerResponse = await client.GetAsync("/api/customers/00000000-0000-0000-0000-000000000101");
        Assert.Contains(catalogResponse.StatusCode, new[] { HttpStatusCode.BadGateway, HttpStatusCode.ServiceUnavailable });
        Assert.Equal(HttpStatusCode.OK, customerResponse.StatusCode);
    }
    [Fact]
    public async Task InventoryRoutesUseInventoryService()
    {
        await using var legacy = await StartLegacyAsync("legacy");
        await using var catalog = await StartLegacyAsync("catalog");
        await using var inventory = await StartLegacyAsync("inventory");
        await using var gateway = CreateGateway(legacy.Urls.Single(), catalog.Urls.Single(), inventory.Urls.Single());
        using var client = gateway.CreateClient();
        foreach (var path in new[] { "/api/inventory/00000000-0000-0000-0000-000000000201", "/api/inventory/reservations/00000000-0000-0000-0000-000000000301" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal("inventory", response.Headers.GetValues("X-Legacy").Single());
        }
        Assert.Equal("catalog", (await client.GetAsync("/api/products")).Headers.GetValues("X-Legacy").Single());
        Assert.Equal("legacy", (await client.GetAsync("/api/customers/00000000-0000-0000-0000-000000000101")).Headers.GetValues("X-Legacy").Single());
    }
    [Fact]
    public async Task OrderRoutesUseOrderServiceWhilePaymentStaysLegacy()
    {
        await using var legacy = await StartLegacyAsync("legacy");
        await using var orders = await StartLegacyAsync("orders");
        await using var gateway = CreateGateway(legacy.Urls.Single(), ordersDestination: orders.Urls.Single());
        using var client = gateway.CreateClient();
        Assert.Equal("orders", (await client.GetAsync("/api/orders/00000000-0000-0000-0000-000000000301")).Headers.GetValues("X-Legacy").Single());
        Assert.Equal("legacy", (await client.PostAsJsonAsync("/api/orders/00000000-0000-0000-0000-000000000301/payment", new { succeed = true })).Headers.GetValues("X-Legacy").Single());
    }
    private static WebApplicationFactory<GatewayProgram> CreateGateway(string destination, string? catalogDestination = null, string? inventoryDestination = null, string? ordersDestination = null) =>
        new WebApplicationFactory<GatewayProgram>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ReverseProxy:Clusters:legacy:Destinations:monolith:Address"] = destination,
                    ["ReverseProxy:Clusters:catalog:Destinations:service:Address"] = catalogDestination ?? destination,
                    ["ReverseProxy:Clusters:inventory:Destinations:service:Address"] = inventoryDestination ?? destination,
                    ["ReverseProxy:Clusters:orders:Destinations:service:Address"] = ordersDestination ?? destination,
                    ["ReverseProxy:Routes:orders:ClusterId"] = ordersDestination is null ? "legacy" : "orders",
                    ["ReverseProxy:Routes:orderById:ClusterId"] = ordersDestination is null ? "legacy" : "orders",
                    ["ReverseProxy:Routes:inventory:ClusterId"] = inventoryDestination is null ? "legacy" : "inventory",
                    ["ReverseProxy:Routes:inventoryStock:ClusterId"] = inventoryDestination is null ? "legacy" : "inventory",
                    ["ReverseProxy:Routes:reservationCreate:ClusterId"] = inventoryDestination is null ? "legacy" : "inventory",
                    ["ReverseProxy:Routes:reservationById:ClusterId"] = inventoryDestination is null ? "legacy" : "inventory",
                    ["ReverseProxy:Routes:reservationRelease:ClusterId"] = inventoryDestination is null ? "legacy" : "inventory",
                    ["ReverseProxy:Routes:reservationConfirm:ClusterId"] = inventoryDestination is null ? "legacy" : "inventory"
                })));

    private static async Task<WebApplication> StartLegacyAsync(string identity = "legacy")
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Run(async context =>
        {
            context.Response.StatusCode = context.Request.Query["status"] == "418" ? 418 : 200;
            context.Response.Headers["X-Legacy"] = identity;
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            await context.Response.WriteAsJsonAsync(new
            {
                method = context.Request.Method,
                path = context.Request.Path.Value,
                query = context.Request.QueryString.Value,
                body,
                correlationId = context.Request.Headers["X-Correlation-ID"].ToString(),
                forwardedFor = context.Request.Headers["X-Forwarded-For"].ToString()
            });
        });
        await app.StartAsync();
        return app;
    }
}
