using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LegacyCommerce.IntegrationTests;

public sealed class ApplicationUseCaseTests
{
    [Fact]
    public async Task StableOrderKeyDoesNotReserveTwice()
    {
        await using var factory = LegacyTestFactory.Create();
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        await services.GetRequiredService<CommerceDb>().Database.MigrateAsync();
        var ct = CancellationToken.None;
        var customer = (await services.GetRequiredService<CreateCustomerHandler>()
            .CreateCustomerAsync("Buyer", $"retry-{Guid.NewGuid():N}@example.test", ct)).Value!;
        var product = (await services.GetRequiredService<CreateProductHandler>()
            .CreateProductAsync("Retry product", 5m, ct)).Value!;
        await services.GetRequiredService<AddStockHandler>().AddStockAsync(product.Id, 2, ct);
        var key = Guid.NewGuid();
        var handler = services.GetRequiredService<CreateOrderHandler>();
        var first = await handler.CreateOrderAsync(customer.Id, product.Id, 1, ct, key);
        var retry = await handler.CreateOrderAsync(customer.Id, product.Id, 1, ct, key);
        Assert.Equal(key, first.Value!.Id);
        Assert.Equal(first.Value.Id, retry.Value!.Id);
        Assert.Equal(1, (await services.GetRequiredService<CommerceQueries>().GetStockAsync(product.Id, ct))!.Quantity);
        Assert.Equal(UseCaseFailure.Conflict, (await handler.CreateOrderAsync(customer.Id, product.Id, 2, ct, key)).Failure);
    }
    [Fact]
    public async Task HandlersCreateAndPayOrderWithPriceSnapshot()
    {
        await using var factory = LegacyTestFactory.Create();
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        await services.GetRequiredService<CommerceDb>().Database.MigrateAsync();
        var commands = services.GetRequiredService<CreateCustomerHandler>();
        var products = services.GetRequiredService<CreateProductHandler>();
        var stock = services.GetRequiredService<AddStockHandler>();
        var orders = services.GetRequiredService<CreateOrderHandler>();
        var payments = services.GetRequiredService<ProcessPaymentHandler>();
        var queries = services.GetRequiredService<CommerceQueries>();
        var ct = CancellationToken.None;
        var customer = (await commands.CreateCustomerAsync("Buyer", $"app-{Guid.NewGuid():N}@example.test", ct)).Value!;
        var product = (await products.CreateProductAsync("Application product", 12.25m, ct)).Value!;
        await stock.AddStockAsync(product.Id, 2, ct);
        var orderResult = await orders.CreateOrderAsync(customer.Id, product.Id, 1, ct);
        Assert.Equal(UseCaseFailure.None, orderResult.Failure);
        Assert.Equal(12.25m, orderResult.Value!.UnitPrice);
        var paymentResult = await payments.ProcessPaymentAsync(orderResult.Value.Id, true, ct);
        Assert.True(paymentResult.Value!.Succeeded);
        Assert.Equal(OrderStatuses.Paid, (await queries.GetOrderAsync(orderResult.Value.Id, ct))!.Status);
        Assert.Single(await queries.GetNotificationsAsync(customer.Id, ct));
    }

    [Fact]
    public async Task InsufficientStockAndFailedPaymentPreserveInventory()
    {
        await using var factory = LegacyTestFactory.Create();
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        await services.GetRequiredService<CommerceDb>().Database.MigrateAsync();
        var ct = CancellationToken.None;
        var customer = (await services.GetRequiredService<CreateCustomerHandler>()
            .CreateCustomerAsync("Buyer", $"stock-{Guid.NewGuid():N}@example.test", ct)).Value!;
        var product = (await services.GetRequiredService<CreateProductHandler>()
            .CreateProductAsync("Stock product", 7m, ct)).Value!;
        var orders = services.GetRequiredService<CreateOrderHandler>();
        var unavailable = await orders.CreateOrderAsync(customer.Id, product.Id, 1, ct);
        Assert.Equal(UseCaseFailure.Conflict, unavailable.Failure);
        Assert.Null(unavailable.Value);
        await services.GetRequiredService<AddStockHandler>().AddStockAsync(product.Id, 1, ct);
        var order = (await orders.CreateOrderAsync(customer.Id, product.Id, 1, ct)).Value!;
        var payment = await services.GetRequiredService<ProcessPaymentHandler>()
            .ProcessPaymentAsync(order.Id, false, ct);
        Assert.False(payment.Value!.Succeeded);
        var queries = services.GetRequiredService<CommerceQueries>();
        Assert.Equal(1, (await queries.GetStockAsync(product.Id, ct))!.Quantity);
        Assert.Equal(OrderStatuses.PaymentFailed, (await queries.GetOrderAsync(order.Id, ct))!.Status);
        Assert.Equal("Payment failed", Assert.Single(await queries.GetNotificationsAsync(customer.Id, ct)).Message);
    }
}
