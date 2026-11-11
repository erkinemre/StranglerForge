using System.Net;
using System.Net.Http.Json;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RabbitMQ.Client;
using StranglerForge.Orders.Application;
using StranglerForge.Orders.Infrastructure;
using Xunit;

namespace Orders.LiveBrokerTests;

public sealed class RabbitRecoveryTests
{
    private static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} is required. Run this project only against an isolated live RabbitMQ virtual host and Orders test database.");
    private static WebApplicationFactory<Program> Factory(string host, bool failAudit = false)
    {
        var db = Required("ORDERS_LIVE_TEST_CONNECTION");
        var user = Required("ORDERS_LIVE_RABBIT_USER");
        var password = Required("ORDERS_LIVE_RABBIT_PASSWORD");
        var vhost = Required("ORDERS_LIVE_RABBIT_VHOST");
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Development")
            .UseSetting("ConnectionStrings:Orders", db)
            .UseSetting("FaultInjection:FailOrderAuditConsumer", failAudit.ToString())
            .UseSetting("OrdersInternal:Token", "isolated-live-test")
            .UseSetting("RabbitMq:UseInMemory", "false")
            .UseSetting("RabbitMq:Host", host)
            .UseSetting("RabbitMq:VirtualHost", vhost)
            .UseSetting("RabbitMq:Username", user)
            .UseSetting("RabbitMq:Password", password)
            .ConfigureTestServices(services => { services.RemoveAll<IOrderDependencies>(); services.AddSingleton<IOrderDependencies>(new FakeDependencies()); }));
    }
    [Fact]
    public async Task OfflineOutboxReplaysAndDuplicateDeliveryHasOneAuditEffect()
    {
        var orderId = Guid.NewGuid();
        await using (var offline = Factory("127.0.0.2"))
        {
            using var client = offline.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders") { Content = JsonContent.Create(new { customerId = Guid.NewGuid(), productId = Guid.NewGuid(), quantity = 1 }) };
            request.Headers.Add("Idempotency-Key", orderId.ToString("D"));
            Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(request)).StatusCode);
            using var scope = offline.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrdersDb>();
            Assert.True(await db.Orders.AnyAsync(x => x.Id == orderId));
            Assert.True(await db.Set<OutboxMessage>().AnyAsync());
            Assert.False(await db.Audit.AnyAsync(x => x.OrderId == orderId));
        }
        await using var online = Factory(Required("ORDERS_LIVE_RABBIT_HOST"));
        using var onlineClient = online.CreateClient();
        for (var i = 0; i < 100; i++)
        {
            using var scope = online.Services.CreateScope();
            if (await scope.ServiceProvider.GetRequiredService<OrdersDb>().Audit.AnyAsync(x => x.OrderId == orderId)) break;
            await Task.Delay(200);
        }
        var eventId = Guid.NewGuid();
        var message = new OrderTransitionedV1(eventId, Guid.NewGuid(), orderId, "DuplicateProbe", DateTime.UtcNow);
        var bus = online.Services.GetRequiredService<IBus>();
        await bus.Publish(message, context => context.MessageId = eventId);
        for (var i = 0; i < 100; i++)
        {
            using var scope = online.Services.CreateScope();
            if (await scope.ServiceProvider.GetRequiredService<OrdersDb>().Audit.AnyAsync(x => x.EventId == eventId)) break;
            await Task.Delay(200);
        }
        await bus.Publish(message, context => context.MessageId = eventId);
        await Task.Delay(500);
        using var verify = online.Services.CreateScope();
        var result = verify.ServiceProvider.GetRequiredService<OrdersDb>();
        Assert.True(await result.Audit.AnyAsync(x => x.OrderId == orderId));
        Assert.Equal(1, await result.Audit.CountAsync(x => x.EventId == eventId));
        Assert.Equal(1, await result.Set<InboxState>().CountAsync(x => x.MessageId == eventId));
    }
    [Fact]
    public async Task FailedConsumerMovesEventToErrorQueueAndRecoversAfterRestart()
    {
        var eventId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var host = Required("ORDERS_LIVE_RABBIT_HOST");
        WebApplicationFactory<Program>? recovered = null;
        await using (var failing = Factory(host, failAudit: true))
        {
            using var client = failing.CreateClient();
            await failing.Services.GetRequiredService<IBus>().Publish(new OrderTransitionedV1(eventId, Guid.NewGuid(), orderId, "FailureProbe", DateTime.UtcNow), context => context.MessageId = eventId);
            var connectionFactory = new ConnectionFactory
            {
                HostName = host,
                VirtualHost = Required("ORDERS_LIVE_RABBIT_VHOST"),
                UserName = Required("ORDERS_LIVE_RABBIT_USER"),
                Password = Required("ORDERS_LIVE_RABBIT_PASSWORD")
            };
            await using var connection = await connectionFactory.CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();
            const string errorQueue = "OrderTransitionAudit_error";
            var found = false;
            for (var attempt = 0; attempt < 200; attempt++)
            {
                try { found = (await channel.QueueDeclarePassiveAsync(errorQueue)).MessageCount > 0; }
                catch (RabbitMQ.Client.Exceptions.OperationInterruptedException) { }
                if (found) break;
                await Task.Delay(200);
            }
            using var scope = failing.Services.CreateScope();
            var auditCount = await scope.ServiceProvider.GetRequiredService<OrdersDb>().Audit.CountAsync(x => x.EventId == eventId);
            Assert.True(found, $"Expected failed message in {errorQueue} after bounded retry window; audit rows: {auditCount}.");
            Assert.Equal(0, auditCount);
            recovered = Factory(host);
            using var started = recovered.CreateClient();
        }
        await using var recoveredHost = recovered ?? throw new InvalidOperationException("Recovery host was not started.");
        using var recoveredClient = recoveredHost.CreateClient();
        var recoveryId = Guid.NewGuid();
        await recoveredHost.Services.GetRequiredService<IBus>().Publish(new OrderTransitionedV1(recoveryId, Guid.NewGuid(), orderId, "Recovered", DateTime.UtcNow), context => context.MessageId = recoveryId);
        var processed = false;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            using var scope = recoveredHost.Services.CreateScope();
            processed = await scope.ServiceProvider.GetRequiredService<OrdersDb>().Audit.AnyAsync(x => x.EventId == recoveryId);
            if (processed) break;
            await Task.Delay(200);
        }
        Assert.True(processed, "Consumer did not process a new event after restart.");
    }
    private sealed class FakeDependencies : IOrderDependencies
    {
        public Task<bool> CustomerExistsAsync(Guid id, CancellationToken ct) => Task.FromResult(true);
        public Task<decimal?> ProductPriceAsync(Guid id, CancellationToken ct) => Task.FromResult<decimal?>(10m);
        public Task<ReserveOutcome> ReserveAsync(Guid orderId, Guid productId, int quantity, Guid correlationId, CancellationToken ct) => Task.FromResult(ReserveOutcome.Reserved);
        public Task ReleaseAsync(Guid orderId, Guid correlationId, CancellationToken ct) => Task.CompletedTask;
        public Task ConfirmAsync(Guid orderId, Guid correlationId, CancellationToken ct) => Task.CompletedTask;
        public Task<PaymentOutcome> AuthorizeAsync(Guid orderId, decimal amount, bool succeed, Guid correlationId, CancellationToken ct) => Task.FromResult(PaymentOutcome.Authorized);
        public Task RecordNotificationAsync(Guid orderId, Guid customerId, bool succeeded, Guid correlationId, CancellationToken ct) => Task.CompletedTask;
    }
}
