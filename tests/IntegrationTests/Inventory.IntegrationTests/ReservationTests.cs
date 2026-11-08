using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StranglerForge.Inventory.Application;
using StranglerForge.Inventory.Domain;
using StranglerForge.Inventory.Infrastructure;

[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace Inventory.IntegrationTests;

public sealed class ReservationTests
{
    [Fact]
    public async Task BrokerOutageLeavesCommittedStateAndOutbox()
    {
        var connection = Environment.GetEnvironmentVariable("INVENTORY_BROKER_TEST_CONNECTION")
            ?? throw new InvalidOperationException("INVENTORY_BROKER_TEST_CONNECTION is required.");
        await using var offline = Factory(connection, false);
        using var client = offline.CreateClient();
        var productId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/inventory/{productId}/stock", new { quantity = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/inventory/reservations", new { reservationId, orderId = Guid.NewGuid(), productId, quantity = 1 })).StatusCode);
        using var scope = offline.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDb>();
        Assert.Equal(1, (await db.Stock.SingleAsync(x => x.ProductId == productId)).Reserved);
        Assert.True(await db.Set<OutboxMessage>().AnyAsync());
        Assert.False(await db.Audit.AnyAsync(x => x.ReservationId == reservationId));
    }
    [Fact]
    public async Task FailedTransactionRollsBackStockAndOutbox()
    {
        await using var factory = Factory(Connection);
        using var client = factory.CreateClient();
        var productId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var useCases = scope.ServiceProvider.GetRequiredService<InventoryUseCases>();
            await Assert.ThrowsAsync<DbUpdateException>(() => useCases.AdjustAsync(productId, 1, new string('x', 201), CancellationToken.None));
        }
        using var verify = factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<InventoryDb>();
        Assert.False(await db.Stock.AnyAsync(x => x.ProductId == productId));
        Assert.False(await db.Adjustments.AnyAsync(x => x.ProductId == productId));
    }
    [Fact]
    public async Task MissingCatalogProductCannotCreateStock()
    {
        await using var factory = Factory(Connection, productExists: false);
        using var client = factory.CreateClient();
        var productId = Guid.NewGuid();
        var response = await client.PostAsJsonAsync($"/api/inventory/{productId}/stock", new { quantity = 1 });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<InventoryDb>().Stock.AnyAsync(x => x.ProductId == productId));
    }
    private static WebApplicationFactory<InventoryProgram> Factory(string connection, bool inMemory = true, bool productExists = true) =>
        new WebApplicationFactory<InventoryProgram>().WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Inventory", connection).UseSetting("RabbitMq:UseInMemory", inMemory.ToString())
                .ConfigureTestServices(services => { services.RemoveAll<IInventoryProductCatalog>(); services.AddSingleton<IInventoryProductCatalog>(new AlwaysPresentCatalog(productExists)); }));
    private sealed class AlwaysPresentCatalog(bool exists) : IInventoryProductCatalog
    {
        public Task<bool> ExistsAsync(Guid productId, CancellationToken ct) => Task.FromResult(exists);
    }
    private static string Connection => Environment.GetEnvironmentVariable("INVENTORY_TEST_CONNECTION")
        ?? throw new InvalidOperationException("INVENTORY_TEST_CONNECTION must point to an isolated test database.");

    [Fact]
    public void DomainEnforcesStockAndReservationTransitions()
    {
        var product = Guid.NewGuid();
        var stock = new StockItem(product, 2);
        stock.Reserve(2);
        Assert.Equal(0, stock.Available);
        Assert.Throws<InvalidOperationException>(() => stock.Reserve(1));
        stock.Release(1);
        Assert.Equal(1, stock.Available);
        stock.Confirm(1);
        Assert.Equal(1, stock.Quantity);
        Assert.Equal(0, stock.Reserved);
        Assert.Throws<InvalidOperationException>(() => stock.Adjust(-2));
        var reservation = new StockReservation(Guid.NewGuid(), Guid.NewGuid(), product, 1);
        Assert.True(reservation.Release());
        Assert.False(reservation.Release());
        Assert.Throws<InvalidOperationException>(() => reservation.Confirm());
    }
    [Fact]
    public async Task DuplicateIntegrationMessageHasOneDurableAuditEffect()
    {
        await using var factory = Factory(Connection);
        using var client = factory.CreateClient();
        var bus = factory.Services.GetRequiredService<IBus>();
        var eventId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var message = new InventoryReservedV1(eventId, Guid.NewGuid(), DateTime.UtcNow, 1, reservationId, Guid.NewGuid(), Guid.NewGuid(), 1);
        await bus.Publish(message, context => context.MessageId = eventId);
        for (var attempt = 0; attempt < 50; attempt++)
        {
            using var scope = factory.Services.CreateScope();
            if (await scope.ServiceProvider.GetRequiredService<InventoryDb>().Audit.AnyAsync(x => x.EventId == eventId)) break;
            await Task.Delay(100);
        }
        await bus.Publish(message, context => context.MessageId = eventId);
        await Task.Delay(500);
        using var verify = factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<InventoryDb>();
        Assert.Equal(1, await db.Audit.CountAsync(x => x.EventId == eventId));
        Assert.Equal(1, await db.Set<InboxState>().CountAsync(x => x.MessageId == eventId));
    }
    [Fact]
    public async Task ConcurrentReservationsCannotOversellAndRetriesAreIdempotent()
    {
        await using var factory = Factory(Connection);
        using var client = factory.CreateClient();
        var productId = Guid.NewGuid();
        var stock = await client.PostAsJsonAsync($"/api/inventory/{productId}/stock", new { quantity = 3 });
        Assert.Equal(HttpStatusCode.OK, stock.StatusCode);
        var requests = Enumerable.Range(0, 10).Select(_ => new { reservationId = Guid.NewGuid(), orderId = Guid.NewGuid(), productId, quantity = 1 }).ToArray();
        var responses = await Task.WhenAll(requests.Select(x => client.PostAsJsonAsync("/api/inventory/reservations", x)));
        Assert.Equal(3, responses.Count(x => x.StatusCode == HttpStatusCode.OK));
        Assert.Equal(7, responses.Count(x => x.StatusCode == HttpStatusCode.Conflict));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDb>();
        Assert.Equal(3, await db.Reservations.CountAsync(x => x.ProductId == productId && x.Status == ReservationStatus.Reserved));
        var item = await db.Stock.SingleAsync(x => x.ProductId == productId);
        Assert.Equal(3, item.Reserved);
        Assert.Equal(0, item.Available);
        var winner = requests[Array.FindIndex(responses, x => x.StatusCode == HttpStatusCode.OK)];
        var duplicate = await client.PostAsJsonAsync("/api/inventory/reservations", winner);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Equal(3, await db.Reservations.CountAsync(x => x.ProductId == productId));
    }

    [Fact]
    public async Task ReleaseAndConfirmArePersistentlyIdempotent()
    {
        await using var factory = Factory(Connection);
        using var client = factory.CreateClient();
        var productId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/inventory/{productId}/stock", new { quantity = 2 })).StatusCode);
        var releasedId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/inventory/reservations", new { reservationId = releasedId, orderId = Guid.NewGuid(), productId, quantity = 1 })).StatusCode);
        for (var i = 0; i < 2; i++) Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/inventory/reservations/{releasedId}/release", new { })).StatusCode);
        var confirmedId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/inventory/reservations", new { reservationId = confirmedId, orderId = Guid.NewGuid(), productId, quantity = 1 })).StatusCode);
        for (var i = 0; i < 2; i++) Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/inventory/reservations/{confirmedId}/confirm", new { })).StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDb>();
        var stock = await db.Stock.SingleAsync(x => x.ProductId == productId);
        Assert.Equal(1, stock.Quantity);
        Assert.Equal(0, stock.Reserved);
        Assert.Equal(ReservationStatus.Released, (await db.Reservations.SingleAsync(x => x.Id == releasedId)).Status);
        Assert.Equal(ReservationStatus.Confirmed, (await db.Reservations.SingleAsync(x => x.Id == confirmedId)).Status);
    }
}
