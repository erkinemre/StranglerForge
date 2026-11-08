using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Xunit;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StranglerForge.Orders.Application;
using StranglerForge.Orders.Domain;
using StranglerForge.Orders.Infrastructure;

[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
namespace Orders.IntegrationTests;

public sealed class OrderWorkflowTests
{
    private static string Connection => Environment.GetEnvironmentVariable("ORDERS_TEST_CONNECTION") ?? throw new InvalidOperationException("ORDERS_TEST_CONNECTION required.");
    private static (WebApplicationFactory<Program> Factory, FakeDependencies Fake) Create(ReserveOutcome reserve = ReserveOutcome.Reserved, PaymentOutcome payment = PaymentOutcome.Authorized, int reserveOutages = 0, int releaseOutages = 0, int paymentOutages = 0)
    {
        var fake = new FakeDependencies(reserve, payment, reserveOutages, releaseOutages, paymentOutages);
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Orders", Connection).UseSetting("RabbitMq:UseInMemory", "true").UseSetting("OrdersInternal:Token", "test-token")
            .ConfigureTestServices(services => { services.RemoveAll<IOrderDependencies>(); services.AddSingleton<IOrderDependencies>(fake); }));
        return (factory, fake);
    }
    private static async Task<OrderSnapshot> WaitForAsync(HttpClient client, Guid id, params string[] states)
    {
        for (var i = 0; i < 100; i++)
        {
            var order = await client.GetFromJsonAsync<OrderSnapshot>($"/api/orders/{id}");
            if (order is not null && states.Contains(order.Status)) return order;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Order {id} did not reach {string.Join(',', states)}.");
    }
    private static async Task<Guid> Submit(HttpClient client, Guid? id = null)
    {
        var key = id ?? Guid.NewGuid();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders") { Content = JsonContent.Create(new { customerId = Guid.NewGuid(), productId = Guid.NewGuid(), quantity = 1 }) };
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OrderSnapshot>())!.Id;
    }
    private static async Task SetPayment(HttpClient client, Guid id, bool succeed)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/internal/orders/{id}/payment-intent") { Content = JsonContent.Create(new { succeed }) };
        request.Headers.Add("X-Orders-Internal-Token", "test-token");
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(request)).StatusCode);
    }
    [Fact]
    public async Task PaidOrderUsesDurableSagaAndIdempotentReservation()
    {
        var (factory, fake) = Create(); await using var owned = factory;
        using var client = factory.CreateClient();
        var id = await Submit(client);
        await SetPayment(client, id, true);
        var confirmed = await WaitForAsync(client, id, nameof(OrderStatus.Confirmed));
        Assert.Equal(id, confirmed.Id);
        Assert.Equal(1, fake.ConfirmCalls);
        Assert.Equal(0, fake.ReleaseCalls);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDb>();
        Assert.Equal(OrderStatus.Confirmed, (await db.Sagas.SingleAsync(x => x.OrderId == id)).State);
        Assert.Equal(1, await db.Items.CountAsync(x => x.OrderId == id && x.Quantity == 1 && x.UnitPrice == 12m));
    }
    [Fact]
    public async Task InsufficientInventoryFailsWithoutPaymentOrRelease()
    {
        var (factory, fake) = Create(ReserveOutcome.Rejected); await using var owned = factory;
        using var client = factory.CreateClient();
        var id = await Submit(client);
        await WaitForAsync(client, id, nameof(OrderStatus.Failed));
        Assert.Equal(0, fake.PaymentCalls);
        Assert.Equal(0, fake.ReleaseCalls);
    }
    [Fact]
    public async Task PaymentRejectionCompensatesOnce()
    {
        var (factory, fake) = Create(payment: PaymentOutcome.Rejected); await using var owned = factory;
        using var client = factory.CreateClient();
        var id = await Submit(client);
        await SetPayment(client, id, false);
        await WaitForAsync(client, id, nameof(OrderStatus.Cancelled));
        Assert.Equal(1, fake.ReleaseCalls);
    }
    [Fact]
    public async Task UncertainPaymentRemainsPendingWithoutRelease()
    {
        var (factory, fake) = Create(payment: PaymentOutcome.Uncertain); await using var owned = factory;
        using var client = factory.CreateClient();
        var id = await Submit(client);
        await SetPayment(client, id, true);
        await WaitForAsync(client, id, nameof(OrderStatus.PaymentPending));
        for (var i = 0; i < 30; i++) { using var scope = factory.Services.CreateScope(); if ((await scope.ServiceProvider.GetRequiredService<OrdersDb>().Sagas.SingleAsync(x => x.OrderId == id)).LastError is not null) break; await Task.Delay(100); }
        using var verify = factory.Services.CreateScope();
        Assert.Contains("manual review", (await verify.ServiceProvider.GetRequiredService<OrdersDb>().Sagas.SingleAsync(x => x.OrderId == id)).LastError);
        Assert.Equal(0, fake.ReleaseCalls);
    }
    [Fact]
    public void AggregateRejectsInvalidValuesAndInvalidTransition()
    {
        Assert.Throws<ArgumentException>(() => Order.Submit(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), 1, 1m, DateTime.UtcNow));
        var order = Order.Submit(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 2m, DateTime.UtcNow);
        Assert.Throws<InvalidOperationException>(() => order.Transition(OrderStatus.PaymentPending, OrderStatus.Confirmed, DateTime.UtcNow));
        Assert.Throws<InvalidOperationException>(() => order.Transition(OrderStatus.Submitted, OrderStatus.Confirmed, DateTime.UtcNow));
        Assert.Equal(2m, order.Total);
        Assert.Equal(2m, order.Items.Single().Total);
    }
    [Fact]
    public async Task DuplicateTransitionEventHasOneDurableAuditEffect()
    {
        var (factory, _) = Create(); await using var owned = factory;
        using var client = factory.CreateClient();
        var bus = factory.Services.GetRequiredService<IBus>();
        var eventId = Guid.NewGuid();
        var message = new OrderTransitionedV1(eventId, Guid.NewGuid(), Guid.NewGuid(), "Submitted", DateTime.UtcNow);
        await bus.Publish(message, context => context.MessageId = eventId);
        for (var i = 0; i < 50; i++)
        {
            using var scope = factory.Services.CreateScope();
            if (await scope.ServiceProvider.GetRequiredService<OrdersDb>().Audit.AnyAsync(x => x.EventId == eventId)) break;
            await Task.Delay(100);
        }
        await bus.Publish(message, context => context.MessageId = eventId);
        await Task.Delay(500);
        using var verify = factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<OrdersDb>();
        Assert.Equal(1, await db.Audit.CountAsync(x => x.EventId == eventId));
        Assert.Equal(1, await db.Set<InboxState>().CountAsync(x => x.MessageId == eventId));
    }    [Fact]
    public async Task PendingSagaResumesAfterHostRestart()
    {
        Guid id;
        var first = Create();
        await using (first.Factory)
        {
            using var client = first.Factory.CreateClient();
            id = await Submit(client);
            await WaitForAsync(client, id, nameof(OrderStatus.PaymentPending));
        }
        var second = Create();
        await using (second.Factory)
        {
            using var client = second.Factory.CreateClient();
            await SetPayment(client, id, true);
            await WaitForAsync(client, id, nameof(OrderStatus.Confirmed));
            Assert.Equal(1, second.Fake.ConfirmCalls);
        }
    }
    [Fact]
    public async Task ConcurrentDuplicateRequestsPersistOneOrder()
    {
        var (factory, _) = Create(); await using var owned = factory;
        using var client = factory.CreateClient();
        var id = Guid.NewGuid(); var customerId = Guid.NewGuid(); var productId = Guid.NewGuid();
        async Task<HttpResponseMessage> Send()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders") { Content = JsonContent.Create(new { customerId, productId, quantity = 1 }) };
            request.Headers.Add("Idempotency-Key", id.ToString("D"));
            return await client.SendAsync(request);
        }
        var responses = await Task.WhenAll(Send(), Send());
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OrdersDb>().Orders.CountAsync(x => x.Id == id));
    }    [Fact]
    public async Task HttpCorrelationFlowsToSagaDependencyAndEvent()
    {
        var (factory, fake) = Create(); await using var owned = factory;
        using var client = factory.CreateClient();
        var orderId = Guid.NewGuid(); var correlationId = Guid.NewGuid();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders") { Content = JsonContent.Create(new { customerId = Guid.NewGuid(), productId = Guid.NewGuid(), quantity = 1 }) };
        request.Headers.Add("Idempotency-Key", orderId.ToString("D"));
        request.Headers.Add("X-Correlation-ID", correlationId.ToString("D"));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(correlationId.ToString("D"), response.Headers.GetValues("X-Correlation-ID").Single());
        await WaitForAsync(client, orderId, nameof(OrderStatus.PaymentPending));
        for (var i = 0; i < 50; i++)
        {
            using var scope = factory.Services.CreateScope();
            if (await scope.ServiceProvider.GetRequiredService<OrdersDb>().Audit.AnyAsync(x => x.OrderId == orderId)) break;
            await Task.Delay(100);
        }
        using var verify = factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<OrdersDb>();
        Assert.Equal(correlationId, (await db.Sagas.SingleAsync(x => x.OrderId == orderId)).CorrelationId);
        Assert.Equal(correlationId, fake.LastCorrelationId);
        Assert.All(await db.Audit.Where(x => x.OrderId == orderId).ToListAsync(), row => Assert.Equal(correlationId, row.CorrelationId));
    }    [Fact]
    public async Task InventoryOutageRetriesStableReservation()
    {
        var (factory, fake) = Create(reserveOutages: 1); await using var owned = factory;
        using var client = factory.CreateClient();
        var id = await Submit(client);
        await WaitForAsync(client, id, nameof(OrderStatus.PaymentPending));
        Assert.True(fake.ReserveCalls >= 2);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OrdersDb>().Orders.CountAsync(x => x.Id == id));
    }
    [Fact]
    public async Task PaymentTimeoutRetriesIdempotentAuthorization()
    {
        var (factory, fake) = Create(paymentOutages: 1); await using var owned = factory;
        using var client = factory.CreateClient();
        var id = await Submit(client);
        await SetPayment(client, id, true);
        await WaitForAsync(client, id, nameof(OrderStatus.Confirmed));
        Assert.True(fake.PaymentCalls >= 2);
        Assert.Equal(0, fake.ReleaseCalls);
    }
    [Fact]
    public async Task CompensationOutageRetriesRelease()
    {
        var (factory, fake) = Create(payment: PaymentOutcome.Rejected, releaseOutages: 1); await using var owned = factory;
        using var client = factory.CreateClient();
        var id = await Submit(client);
        await SetPayment(client, id, false);
        await WaitForAsync(client, id, nameof(OrderStatus.Cancelled));
        Assert.True(fake.ReleaseCalls >= 2);
    }    private sealed class FakeDependencies(ReserveOutcome reserve, PaymentOutcome payment, int reserveOutages, int releaseOutages, int paymentOutages) : IOrderDependencies
    {
        public int ReleaseCalls;
        public int ConfirmCalls;
        public int PaymentCalls;
        public int ReserveCalls;
        private int _reserveOutages = reserveOutages;
        private int _releaseOutages = releaseOutages;
        private int _paymentOutages = paymentOutages;
        public Guid LastCorrelationId;
        public Task<bool> CustomerExistsAsync(Guid id, CancellationToken ct) => Task.FromResult(true);
        public Task<decimal?> ProductPriceAsync(Guid id, CancellationToken ct) => Task.FromResult<decimal?>(12m);
        public Task<ReserveOutcome> ReserveAsync(Guid orderId, Guid productId, int quantity, Guid correlationId, CancellationToken ct) { Interlocked.Increment(ref ReserveCalls); LastCorrelationId = correlationId; if (Interlocked.Decrement(ref _reserveOutages) >= 0) throw new DependencyUnavailableException("Inventory offline"); return Task.FromResult(reserve); }
        public Task ReleaseAsync(Guid orderId, Guid correlationId, CancellationToken ct) { Interlocked.Increment(ref ReleaseCalls); if (Interlocked.Decrement(ref _releaseOutages) >= 0) throw new DependencyUnavailableException("Inventory offline"); return Task.CompletedTask; }
        public Task ConfirmAsync(Guid orderId, Guid correlationId, CancellationToken ct) { Interlocked.Increment(ref ConfirmCalls); return Task.CompletedTask; }
        public Task<PaymentOutcome> AuthorizeAsync(Guid orderId, decimal amount, bool succeed, Guid correlationId, CancellationToken ct) { Interlocked.Increment(ref PaymentCalls); if (Interlocked.Decrement(ref _paymentOutages) >= 0) throw new DependencyUnavailableException("Payment response timed out"); return Task.FromResult(payment); }
        public Task RecordNotificationAsync(Guid orderId, Guid customerId, bool succeeded, Guid correlationId, CancellationToken ct) => Task.CompletedTask;
    }
}
