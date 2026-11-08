using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using StranglerForge.Catalog.Infrastructure;
using StranglerForge.Inventory.Domain;
using StranglerForge.Inventory.Infrastructure;

var mode = args.SingleOrDefault();
if (mode is not "--import" and not "--verify") { Console.Error.WriteLine("Usage: Inventory.Migration --import|--verify"); return 2; }
if (mode == "--import" && Environment.GetEnvironmentVariable("INVENTORY_WRITE_FREEZE_ACK") != "true")
{ Console.Error.WriteLine("Import requires INVENTORY_WRITE_FREEZE_ACK=true after freezing legacy inventory and order writes."); return 2; }
var sourceString = Environment.GetEnvironmentVariable("ConnectionStrings__LegacyCommerce");
var targetString = Environment.GetEnvironmentVariable("ConnectionStrings__Inventory");
var catalogString = Environment.GetEnvironmentVariable("ConnectionStrings__Catalog");
if (string.IsNullOrWhiteSpace(sourceString) || string.IsNullOrWhiteSpace(targetString) || string.IsNullOrWhiteSpace(catalogString))
{ Console.Error.WriteLine("LegacyCommerce, Inventory, and Catalog connection strings are required."); return 2; }
try
{
    await using var source = new CommerceDb(new DbContextOptionsBuilder<CommerceDb>().UseSqlServer(sourceString).Options);
    await using var target = new InventoryDb(new DbContextOptionsBuilder<InventoryDb>().UseNpgsql(targetString).Options);
    await using var catalog = new CatalogDb(new DbContextOptionsBuilder<CatalogDb>().UseNpgsql(catalogString).Options);
    await WaitUntilReadyAsync(source, "Legacy SQL Server");
    await WaitUntilReadyAsync(catalog, "Catalog PostgreSQL");
    await WaitUntilReadyAsync(target, "Inventory PostgreSQL");
    if (mode == "--import") await target.Database.MigrateAsync();
    var sourceStock = await source.Stock.AsNoTracking().OrderBy(x => x.ProductId).ToListAsync();
    var pending = await source.Orders.AsNoTracking().Where(x => x.Status == OrderStatuses.PendingPayment).OrderBy(x => x.Id).ToListAsync();
    var catalogIds = (await catalog.Products.AsNoTracking().Select(x => x.Id).ToListAsync()).ToHashSet();
    var missing = sourceStock.Select(x => x.ProductId).Concat(pending.Select(x => x.ProductId)).Distinct().Where(x => !catalogIds.Contains(x)).ToList();
    Console.WriteLine($"sourceStock={sourceStock.Count} pendingOrders={pending.Count} catalogProducts={catalogIds.Count} missingCatalogIds={missing.Count}");
    if (missing.Count > 0)
    {
        var missingProducts = await source.Products.AsNoTracking().Where(x => missing.Contains(x.Id)).OrderBy(x => x.Id).ToListAsync();
        var relatedOrders = await source.Orders.AsNoTracking().Where(x => missing.Contains(x.ProductId)).ToListAsync();
        foreach (var product in missingProducts) Console.WriteLine($"missingCatalogProduct={product.Id:D} name={product.Name} orderCount={relatedOrders.Count(x => x.ProductId == product.Id)} pendingOrderCount={relatedOrders.Count(x => x.ProductId == product.Id && x.Status == OrderStatuses.PendingPayment)}");
        throw new InvalidOperationException($"{missing.Count} stock/order product identifiers missing from Catalog: {string.Join(",", missing)}.");
    }
    if (sourceStock.Any(x => x.Quantity < 0) || sourceStock.GroupBy(x => x.ProductId).Any(g => g.Count() > 1)) throw new InvalidOperationException("Invalid or duplicate source stock.");
    var pendingByProduct = pending.GroupBy(x => x.ProductId).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
    if (pendingByProduct.Keys.Except(sourceStock.Select(x => x.ProductId)).Any()) throw new InvalidOperationException("Pending orders reference absent source stock.");
    var sourceFingerprint = Fingerprint(sourceStock, pending);
    Console.WriteLine($"sourceStock={sourceStock.Count} sourcePendingReservations={pending.Count} sourceAvailable={sourceStock.Sum(x => (long)x.Quantity)} fingerprint={sourceFingerprint}");
    var targetStock = await target.Stock.AsNoTracking().OrderBy(x => x.ProductId).ToListAsync();
    var targetReservations = await target.Reservations.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
    if (mode == "--import" && targetStock.Count == 0 && targetReservations.Count == 0)
    {
        await using var transaction = await target.Database.BeginTransactionAsync();
        foreach (var row in sourceStock)
        {
            var item = new StranglerForge.Inventory.Domain.StockItem(row.ProductId, checked(row.Quantity + pendingByProduct.GetValueOrDefault(row.ProductId)));
            if (pendingByProduct.TryGetValue(row.ProductId, out var reserved)) item.Reserve(reserved);
            target.Stock.Add(item);
        }
        foreach (var order in pending) target.Reservations.Add(new StockReservation(order.Id, order.Id, order.ProductId, order.Quantity));
        await target.SaveChangesAsync();
        var afterStock = await source.Stock.AsNoTracking().OrderBy(x => x.ProductId).ToListAsync();
        var afterPending = await source.Orders.AsNoTracking().Where(x => x.Status == OrderStatuses.PendingPayment).OrderBy(x => x.Id).ToListAsync();
        if (Fingerprint(afterStock, afterPending) != sourceFingerprint) throw new InvalidOperationException("Source changed during import; target rolled back.");
        await transaction.CommitAsync();
        targetStock = await target.Stock.AsNoTracking().OrderBy(x => x.ProductId).ToListAsync();
        targetReservations = await target.Reservations.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
    }
    var expectedStock = sourceStock.ToDictionary(x => x.ProductId, x => (Total: x.Quantity + pendingByProduct.GetValueOrDefault(x.ProductId), Available: x.Quantity));
    if (targetStock.Count != expectedStock.Count || targetStock.Any(x => !expectedStock.TryGetValue(x.ProductId, out var expected) || x.Quantity != expected.Total || x.Available != expected.Available))
        throw new InvalidOperationException("Stock totals or product IDs diverged.");
    if (targetReservations.Count != pending.Count || targetReservations.Any(x => !pending.Any(o => o.Id == x.Id && o.ProductId == x.ProductId && o.Quantity == x.Quantity && x.Status == ReservationStatus.Reserved)))
        throw new InvalidOperationException("Active reservations diverged.");
    Console.WriteLine($"cutoverReady=true targetStock={targetStock.Count} targetReservations={targetReservations.Count} preservedProductIds=true availableTotal={targetStock.Sum(x => (long)x.Available)}");
    return 0;
}
catch (Exception exception) { Console.Error.WriteLine($"cutoverReady=false error={exception.Message} root={exception.GetBaseException().Message}"); return 1; }

static string Fingerprint(IEnumerable<StockItem> stock, IEnumerable<CommerceOrder> pending)
{
    var rows = stock.OrderBy(x => x.ProductId).Select(x => $"S|{x.ProductId:D}|{x.Quantity}")
        .Concat(pending.OrderBy(x => x.Id).Select(x => $"R|{x.Id:D}|{x.ProductId:D}|{x.Quantity}"));
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", rows))));
}





static async Task WaitUntilReadyAsync(DbContext context, string label)
{
    string? lastFailure = null;
    const int attempts = 20;
    for (var attempt = 1; attempt <= attempts; attempt++)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await context.Database.OpenConnectionAsync(timeout.Token);
            await context.Database.CloseConnectionAsync();
            return;
        }
        catch (Exception exception)
        {
            lastFailure = exception.GetBaseException().Message;
            Console.Error.WriteLine($"readiness={label} attempt={attempt}/{attempts} error={lastFailure}");
            if (attempt < attempts) await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }
    throw new InvalidOperationException($"{label} unavailable after {attempts} attempts. Last failure: {lastFailure}");
}
