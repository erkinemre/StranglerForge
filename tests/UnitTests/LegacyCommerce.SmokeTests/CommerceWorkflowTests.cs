using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LegacyCommerce.SmokeTests;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LegacyCommerce.IntegrationTests;

public sealed class CommerceWorkflowTests
{
    [Fact]
    public async Task CustomerCanBuySeededProductAndReceiveNotification()
    {
        await using var factory = LegacyTestFactory.Create();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommerceDb>();
        await db.Database.MigrateAsync();
        var client = factory.CreateClient();
        var email = $"buyer-{Guid.NewGuid():N}@example.test";
        var customerResponse = await client.PostAsJsonAsync("/api/customers", new { name = "Buyer", email });
        Assert.Equal(HttpStatusCode.Created, customerResponse.StatusCode);
        var customer = await customerResponse.Content.ReadFromJsonAsync<JsonElement>();
        var customerId = customer.GetProperty("id").GetGuid();
        var productId = Guid.Parse("00000000-0000-0000-0000-000000000201");
        var productResponse = await client.GetAsync("/api/products");
        Assert.Equal(HttpStatusCode.OK, productResponse.StatusCode);
        var stockResponse = await client.PostAsJsonAsync($"/api/inventory/{productId}/stock", new { quantity = 1 });
        Assert.Equal(HttpStatusCode.OK, stockResponse.StatusCode);
        var orderResponse = await client.PostAsJsonAsync("/api/orders", new { customerId, productId, quantity = 1 });
        Assert.Equal(HttpStatusCode.Created, orderResponse.StatusCode);
        var order = await orderResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = order.GetProperty("id").GetGuid();
        var paymentResponse = await client.PostAsJsonAsync($"/api/orders/{orderId}/payment", new { succeed = true });
        Assert.Equal(HttpStatusCode.OK, paymentResponse.StatusCode);
        var duplicateResponse = await client.PostAsJsonAsync($"/api/orders/{orderId}/payment", new { succeed = true });
        Assert.Equal(HttpStatusCode.Conflict, duplicateResponse.StatusCode);
        var notifications = await client.GetFromJsonAsync<JsonElement>($"/api/notifications/{customerId}");
        Assert.Equal(1, notifications.GetArrayLength());
        Assert.Equal("Order paid", notifications[0].GetProperty("message").GetString());
    }
}
