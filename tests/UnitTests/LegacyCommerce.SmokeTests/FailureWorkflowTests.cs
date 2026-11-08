using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LegacyCommerce.IntegrationTests;

public sealed class FailureWorkflowTests
{
    [Fact]
    public async Task FailedPaymentReleasesStockAndNotificationCanBeDispatched()
    {
        await using var factory = LegacyTestFactory.Create();
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CommerceDb>().Database.MigrateAsync();
        var client = factory.CreateClient();
        var email = $"failure-{Guid.NewGuid():N}@example.test";
        var customerResponse = await client.PostAsJsonAsync("/api/customers", new { name = "Failure Buyer", email });
        var customer = await customerResponse.Content.ReadFromJsonAsync<JsonElement>();
        var customerId = customer.GetProperty("id").GetGuid();
        var productResponse = await client.PostAsJsonAsync("/api/products", new { name = "Test Product", price = 9.50m });
        var product = await productResponse.Content.ReadFromJsonAsync<JsonElement>();
        var productId = product.GetProperty("id").GetGuid();
        var noStock = await client.PostAsJsonAsync("/api/orders", new { customerId, productId, quantity = 1 });
        Assert.Equal(HttpStatusCode.Conflict, noStock.StatusCode);
        var stockResponse = await client.PostAsJsonAsync($"/api/inventory/{productId}/stock", new { quantity = 1 });
        Assert.Equal(HttpStatusCode.OK, stockResponse.StatusCode);
        var orderResponse = await client.PostAsJsonAsync("/api/orders", new { customerId, productId, quantity = 1 });
        Assert.Equal(HttpStatusCode.Created, orderResponse.StatusCode);
        var order = await orderResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = order.GetProperty("id").GetGuid();
        var payment = await client.PostAsJsonAsync($"/api/orders/{orderId}/payment", new { succeed = false });
        Assert.Equal(HttpStatusCode.OK, payment.StatusCode);
        var inventory = await client.GetFromJsonAsync<JsonElement>($"/api/inventory/{productId}");
        Assert.Equal(1, inventory.GetProperty("quantity").GetInt32());
        var notifications = await client.GetFromJsonAsync<JsonElement>($"/api/notifications/{customerId}");
        var notificationId = notifications[0].GetProperty("id").GetGuid();
        Assert.Equal("Pending", notifications[0].GetProperty("status").GetString());
        var dispatch = await client.PostAsync($"/api/notifications/{notificationId}/dispatch", null);
        Assert.Equal(HttpStatusCode.NoContent, dispatch.StatusCode);
        var after = await client.GetFromJsonAsync<JsonElement>($"/api/notifications/{customerId}");
        Assert.Equal("Sent", after[0].GetProperty("status").GetString());
    }
}
