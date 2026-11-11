using System.Diagnostics.Metrics;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using StranglerForge.Inventory.Application;
using StranglerForge.Inventory.Domain;

namespace StranglerForge.Inventory.Infrastructure;

public sealed class InventoryStore(InventoryDb db, IPublishEndpoint publisher, IInventoryCorrelation correlation) : IInventoryStore
{
    private static readonly Meter Meter = new("StranglerForge.Inventory");
    private static readonly Counter<long> Duplicates = Meter.CreateCounter<long>("stranglerforge.inventory.duplicates");
    private static readonly Counter<long> ConcurrencyConflicts = Meter.CreateCounter<long>("stranglerforge.inventory.concurrency_conflicts");
    private DbSet<StockItem> Stock => db.Stock;
    private DbSet<StockReservation> Reservations => db.Reservations;
    private DbSet<InventoryAdjustment> Adjustments => db.Adjustments;
    private DbSet<InventoryTransaction> Transactions => db.Transactions;
    private DatabaseFacade Database => db.Database;
    private ChangeTracker ChangeTracker => db.ChangeTracker;
    private Task<int> SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);    public async Task<StockAvailability?> GetStockAsync(Guid id, CancellationToken ct) =>
        await Stock.AsNoTracking().Where(x => x.ProductId == id).Select(x => new StockAvailability(x.ProductId, x.Quantity, x.Reserved, x.Quantity - x.Reserved)).SingleOrDefaultAsync(ct);
    public async Task<ReservationSnapshot?> GetReservationAsync(Guid id, CancellationToken ct) =>
        await Reservations.AsNoTracking().Where(x => x.Id == id).Select(x => new ReservationSnapshot(x.Id, x.OrderId, x.ProductId, x.Quantity, x.Status)).SingleOrDefaultAsync(ct);

    public async Task<InventoryResult<ReservationSnapshot>> ReserveAsync(ReserveStock command, CancellationToken ct)
    {
        if (command.ReservationId == Guid.Empty || command.OrderId == Guid.Empty || command.ProductId == Guid.Empty || command.Quantity <= 0)
            return InventoryResult<ReservationSnapshot>.Fail("Invalid reservation.");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var existing = await GetReservationAsync(command.ReservationId, ct);
            if (existing is not null)
            {
                Duplicates.Add(1);
                return existing.OrderId == command.OrderId && existing.ProductId == command.ProductId && existing.Quantity == command.Quantity
                    ? InventoryResult<ReservationSnapshot>.Ok(existing) : InventoryResult<ReservationSnapshot>.Fail("Reservation identifier conflict.");
            }
            try
            {
                await using var transaction = await Database.BeginTransactionAsync(ct);
                var changed = await Stock.Where(x => x.ProductId == command.ProductId && x.Quantity - x.Reserved >= command.Quantity)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Reserved, x => x.Reserved + command.Quantity), ct);
                if (changed == 0) return InventoryResult<ReservationSnapshot>.Fail("Insufficient stock.");
                var reservation = new StockReservation(command.ReservationId, command.OrderId, command.ProductId, command.Quantity);
                Reservations.Add(reservation);
                Transactions.Add(new InventoryTransaction { ProductId = command.ProductId, ReservationId = command.ReservationId, Kind = "Reserved", Quantity = command.Quantity });
                await publisher.Publish(new InventoryReservedV1(Guid.NewGuid(), correlation.CorrelationId, DateTime.UtcNow, 1, command.ReservationId, command.OrderId, command.ProductId, command.Quantity), ct);
                await SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return InventoryResult<ReservationSnapshot>.Ok(new(command.ReservationId, command.OrderId, command.ProductId, command.Quantity, ReservationStatus.Reserved));
            }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                Duplicates.Add(1);
                ChangeTracker.Clear();
                var duplicate = await GetReservationAsync(command.ReservationId, ct);
                if (duplicate is not null) return duplicate.OrderId == command.OrderId && duplicate.ProductId == command.ProductId && duplicate.Quantity == command.Quantity
                    ? InventoryResult<ReservationSnapshot>.Ok(duplicate) : InventoryResult<ReservationSnapshot>.Fail("Reservation identifier conflict.");
                return InventoryResult<ReservationSnapshot>.Fail("Order already has a reservation.");
            }
            catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected && attempt < 2)
            {
                ConcurrencyConflicts.Add(1);
                ChangeTracker.Clear();
                await Task.Delay(TimeSpan.FromMilliseconds(40 * (attempt + 1)), ct);
            }
        }
        return InventoryResult<ReservationSnapshot>.Fail("Reservation could not be committed.");
    }

    public Task<InventoryResult<ReservationSnapshot>> ReleaseAsync(Guid id, CancellationToken ct) => TransitionAsync(id, false, ct);
    public Task<InventoryResult<ReservationSnapshot>> ConfirmAsync(Guid id, CancellationToken ct) => TransitionAsync(id, true, ct);
    private async Task<InventoryResult<ReservationSnapshot>> TransitionAsync(Guid id, bool confirm, CancellationToken ct)
    {
        await using var transaction = await Database.BeginTransactionAsync(ct);
        var reservation = await Reservations.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (reservation is null) return InventoryResult<ReservationSnapshot>.Fail("Reservation does not exist.");
        var expected = confirm ? ReservationStatus.Confirmed : ReservationStatus.Released;
        if (reservation.Status == expected) return InventoryResult<ReservationSnapshot>.Ok(new(id, reservation.OrderId, reservation.ProductId, reservation.Quantity, expected));
        if (reservation.Status != ReservationStatus.Reserved) return InventoryResult<ReservationSnapshot>.Fail("Reservation already finalized.");
        var changed = await Reservations.Where(x => x.Id == id && x.Status == ReservationStatus.Reserved)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, expected), ct);
        if (changed == 0) { ConcurrencyConflicts.Add(1); return InventoryResult<ReservationSnapshot>.Fail("Concurrent reservation transition."); }
        var stockChanged = confirm
            ? await Stock.Where(x => x.ProductId == reservation.ProductId && x.Reserved >= reservation.Quantity)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Reserved, x => x.Reserved - reservation.Quantity).SetProperty(x => x.Quantity, x => x.Quantity - reservation.Quantity), ct)
            : await Stock.Where(x => x.ProductId == reservation.ProductId && x.Reserved >= reservation.Quantity)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Reserved, x => x.Reserved - reservation.Quantity), ct);
        if (stockChanged != 1) throw new InvalidOperationException("Stock and reservation diverged.");
        Transactions.Add(new InventoryTransaction { ProductId = reservation.ProductId, ReservationId = id, Kind = confirm ? "Confirmed" : "Released", Quantity = reservation.Quantity });
        if (confirm) await publisher.Publish(new InventoryReservationConfirmedV1(Guid.NewGuid(), correlation.CorrelationId, DateTime.UtcNow, 1, id, reservation.OrderId, reservation.ProductId, reservation.Quantity), ct);
        else await publisher.Publish(new InventoryReservationReleasedV1(Guid.NewGuid(), correlation.CorrelationId, DateTime.UtcNow, 1, id, reservation.OrderId, reservation.ProductId, reservation.Quantity), ct);
        await SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return InventoryResult<ReservationSnapshot>.Ok(new(id, reservation.OrderId, reservation.ProductId, reservation.Quantity, expected));
    }

    public async Task<InventoryResult<StockAvailability>> AdjustAsync(Guid productId, int delta, string reason, CancellationToken ct)
    {
        if (productId == Guid.Empty || delta == 0 || string.IsNullOrWhiteSpace(reason)) return InventoryResult<StockAvailability>.Fail("Invalid adjustment.");
        await using var transaction = await Database.BeginTransactionAsync(ct);
        await Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"StockItems\" (\"ProductId\", \"Quantity\", \"Reserved\") VALUES ({productId}, 0, 0) ON CONFLICT (\"ProductId\") DO NOTHING", ct);
        var changed = await Stock.Where(x => x.ProductId == productId && (long)x.Quantity + delta >= x.Reserved && (long)x.Quantity + delta <= int.MaxValue)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Quantity, x => x.Quantity + delta), ct);
        if (changed == 0) return InventoryResult<StockAvailability>.Fail("Insufficient stock or quantity overflow.");
        Adjustments.Add(new InventoryAdjustment { ProductId = productId, Delta = delta, Reason = reason });
        Transactions.Add(new InventoryTransaction { ProductId = productId, Kind = "Adjusted", Quantity = delta });
        var stock = (await GetStockAsync(productId, ct))!;
        await publisher.Publish(new InventoryAdjustedV1(Guid.NewGuid(), correlation.CorrelationId, DateTime.UtcNow, 1, productId, delta, stock.Available), ct);
        await SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return InventoryResult<StockAvailability>.Ok(stock);
    }
}
