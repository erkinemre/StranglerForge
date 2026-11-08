using Microsoft.Extensions.Logging;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using StranglerForge.Orders.Application;
using StranglerForge.Orders.Domain;

namespace StranglerForge.Orders.Infrastructure;

public sealed class OrdersDb(DbContextOptions<OrdersDb> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderSaga> Sagas => Set<OrderSaga>();
    public DbSet<OrderItem> Items => Set<OrderItem>();
    public DbSet<OrderEventAudit> Audit => Set<OrderEventAudit>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Order>(entity => { entity.ToTable("Orders", t => t.HasCheckConstraint("CK_Orders_Positive", "\"Quantity\" > 0 AND \"UnitPrice\" > 0")); entity.HasKey(x => x.Id); entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(40); entity.Property(x => x.UnitPrice).HasPrecision(18, 2); entity.Ignore(x => x.Total); entity.Property(x => x.FailureReason).HasMaxLength(300); entity.Property(x => x.LegacyStatus).HasMaxLength(40); });
        model.Entity<Order>().Navigation(x => x.Items).HasField("_items").UsePropertyAccessMode(PropertyAccessMode.Field);
        model.Entity<OrderItem>(entity => { entity.ToTable("OrderItems", t => t.HasCheckConstraint("CK_OrderItems_Positive", "\"Quantity\" > 0 AND \"UnitPrice\" > 0")); entity.HasKey(x => new { x.OrderId, x.ProductId }); entity.Property(x => x.UnitPrice).HasPrecision(18, 2); entity.Ignore(x => x.Total); entity.HasOne<Order>().WithMany(x => x.Items).HasForeignKey(x => x.OrderId); });
        model.Entity<OrderSaga>(entity => { entity.ToTable("OrderSagas"); entity.HasKey(x => x.OrderId); entity.Property(x => x.State).HasConversion<string>().HasMaxLength(40); entity.Property(x => x.LastError).HasMaxLength(300); });
        model.Entity<OrderEventAudit>(entity => { entity.ToTable("OrderEventAudit"); entity.HasKey(x => x.EventId); entity.HasIndex(x => x.OrderId); entity.Property(x => x.Status).HasMaxLength(40); });
        model.AddInboxStateEntity(); model.AddOutboxMessageEntity(); model.AddOutboxStateEntity();
    }
}

public sealed class EfOrderStore(OrdersDb db, IPublishEndpoint publisher, ILogger<EfOrderStore> logger) : IOrderStore
{
    public Task<Order?> FindAsync(Guid id, CancellationToken ct) => db.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    public Task<OrderSaga?> FindSagaAsync(Guid id, CancellationToken ct) => db.Sagas.AsNoTracking().SingleOrDefaultAsync(x => x.OrderId == id, ct);
    public async Task<bool> TryInsertAsync(Order order, OrderSaga saga, CancellationToken ct)
    {
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.Orders.Add(order); db.Sagas.Add(saga);
            await publisher.Publish(new OrderTransitionedV1(Guid.NewGuid(), saga.CorrelationId, order.Id, order.Status.ToString(), DateTime.UtcNow), ct);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); logger.LogInformation("Order {OrderId} submitted with correlation {CorrelationId}", order.Id, saga.CorrelationId); return true;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) { db.ChangeTracker.Clear(); return false; }
    }
    public async Task<bool> TryTransitionAsync(Guid id, OrderStatus expected, OrderStatus next, string? reason, Guid correlationId, CancellationToken ct)
    {
        if (!OrderLifecycle.CanTransition(expected, next)) throw new InvalidOperationException($"Invalid order transition {expected} to {next}.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var changed = await db.Orders.Where(x => x.Id == id && x.Status == expected).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, next).SetProperty(x => x.UpdatedAtUtc, DateTime.UtcNow).SetProperty(x => x.FailureReason, reason).SetProperty(x => x.LegacyStatus, (string?)null), ct);
        if (changed == 0) return false;
        var sagaChanged = await db.Sagas.Where(x => x.OrderId == id && x.State == expected).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, next).SetProperty(x => x.UpdatedAtUtc, DateTime.UtcNow).SetProperty(x => x.LastError, reason).SetProperty(x => x.Attempts, x => x.Attempts + 1), ct);
        if (sagaChanged != 1) throw new InvalidOperationException("Order and saga state diverged.");
        await publisher.Publish(new OrderTransitionedV1(Guid.NewGuid(), correlationId, id, next.ToString(), DateTime.UtcNow), ct);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); logger.LogInformation("Order {OrderId} moved from {PreviousStatus} to {NextStatus} with correlation {CorrelationId}; reason {Reason}", id, expected, next, correlationId, reason); return true;
    }
    public async Task<IReadOnlyList<Guid>> PendingSagaIdsAsync(int limit, CancellationToken ct) => await db.Sagas.AsNoTracking().Where(x => x.State == OrderStatus.Submitted || x.State == OrderStatus.InventoryPending || x.State == OrderStatus.InventoryReserved || x.State == OrderStatus.PaymentAuthorized || x.State == OrderStatus.CompensationPending || (x.State == OrderStatus.PaymentPending && x.PaymentDecision != null && x.LastError != "Payment outcome uncertain; manual review required") || ((x.State == OrderStatus.Confirmed || x.State == OrderStatus.Cancelled || x.State == OrderStatus.Failed) && !x.NotificationRecorded)).OrderBy(x => x.UpdatedAtUtc).Select(x => x.OrderId).Take(limit).ToListAsync(ct);
    public async Task NoteRetryAsync(Guid id, string reason, CancellationToken ct) => _ = await db.Sagas.Where(x => x.OrderId == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAtUtc, DateTime.UtcNow).SetProperty(x => x.LastError, reason).SetProperty(x => x.Attempts, x => x.Attempts + 1), ct);
    public async Task<bool> SetPaymentDecisionAsync(Guid id, bool succeed, CancellationToken ct) => await db.Sagas.Where(x => x.OrderId == id && x.PaymentDecision == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.PaymentDecision, succeed), ct) == 1;
    public async Task<bool> MarkSagaFlagAsync(Guid id, string flag, CancellationToken ct)
    {
        var query = db.Sagas.Where(x => x.OrderId == id);
        return flag switch
        {
            "reserved" => await query.Where(x => !x.InventoryReserved).ExecuteUpdateAsync(s => s.SetProperty(x => x.InventoryReserved, true), ct) == 1,
            "released" => await query.Where(x => !x.InventoryReleased).ExecuteUpdateAsync(s => s.SetProperty(x => x.InventoryReleased, true), ct) == 1,
            "payment" => await query.Where(x => !x.PaymentRequested).ExecuteUpdateAsync(s => s.SetProperty(x => x.PaymentRequested, true), ct) == 1,
            "notification" => await query.Where(x => !x.NotificationRecorded).ExecuteUpdateAsync(s => s.SetProperty(x => x.NotificationRecorded, true), ct) == 1,
            _ => throw new ArgumentOutOfRangeException(nameof(flag))
        };
    }
}

public static class OrdersInfrastructureRegistration
{
    public static IServiceCollection AddOrdersInfrastructure(this IServiceCollection services, string connection)
    {
        services.AddDbContext<OrdersDb>(o => o.UseNpgsql(connection));
        services.AddScoped<IOrderStore, EfOrderStore>();
        return services;
    }
}

public sealed class OrderEventAudit
{
    public Guid EventId { get; set; }
    public Guid OrderId { get; set; }
    public Guid CorrelationId { get; set; }
    public string Status { get; set; } = "";
    public DateTime OccurredAtUtc { get; set; }
}
