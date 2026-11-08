using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using StranglerForge.Orders.Domain;
using StranglerForge.Orders.Infrastructure;

var mode = args.SingleOrDefault();
if (mode is not "--import" and not "--verify") { Console.Error.WriteLine("Usage: Orders.Migration --import|--verify"); return 2; }
if (mode == "--import" && Environment.GetEnvironmentVariable("ORDERS_WRITE_FREEZE_ACK") != "true") { Console.Error.WriteLine("Import requires ORDERS_WRITE_FREEZE_ACK=true after freezing legacy order and payment writes."); return 2; }
var sql = Environment.GetEnvironmentVariable("ConnectionStrings__LegacyCommerce");
var pg = Environment.GetEnvironmentVariable("ConnectionStrings__Orders");
if (string.IsNullOrWhiteSpace(sql) || string.IsNullOrWhiteSpace(pg)) { Console.Error.WriteLine("LegacyCommerce and Orders connection strings required."); return 2; }
try
{
    await using var source = new CommerceDb(new DbContextOptionsBuilder<CommerceDb>().UseSqlServer(sql).Options);
    await using var target = new OrdersDb(new DbContextOptionsBuilder<OrdersDb>().UseNpgsql(pg).Options);
    if (!await source.Database.CanConnectAsync() || !await target.Database.CanConnectAsync()) throw new InvalidOperationException("Source or target database unavailable.");
    if (mode == "--import") await target.Database.MigrateAsync();
    var sourceOrders = await source.Orders.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
    Console.WriteLine($"sourceOrders={sourceOrders.Count} pending={sourceOrders.Count(x => x.Status == OrderStatuses.PendingPayment)} paid={sourceOrders.Count(x => x.Status == OrderStatuses.Paid)} failed={sourceOrders.Count(x => x.Status == OrderStatuses.PaymentFailed)}");
    var fingerprint = Fingerprint(sourceOrders);
    var targetOrders = await target.Orders.AsNoTracking().ToDictionaryAsync(x => x.Id);
    if (targetOrders.Keys.Except(sourceOrders.Select(x => x.Id)).Any()) throw new InvalidOperationException("Target has service-owned order IDs; migration must not run after cutover.");
    if (mode == "--import")
    {
        await using var tx = await target.Database.BeginTransactionAsync();
        foreach (var row in sourceOrders)
        {
            var status = MapStatus(row.Status);
            if (targetOrders.TryGetValue(row.Id, out var existing))
            {
                if (existing.CustomerId != row.CustomerId || existing.ProductId != row.ProductId || existing.Quantity != row.Quantity || existing.UnitPrice != row.UnitPrice || existing.Status != status || (existing.LegacyStatus is not null && existing.LegacyStatus != row.Status)) throw new InvalidOperationException($"Order {row.Id} diverged.");
                if (existing.LegacyStatus is null) await target.Orders.Where(x => x.Id == row.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.LegacyStatus, row.Status));
                continue;
            }
            var now = DateTime.UtcNow;
            target.Orders.Add(Order.Import(row.Id, row.CustomerId, row.ProductId, row.Quantity, row.UnitPrice, status, row.Status, now));
            var saga = OrderSaga.Start(row.Id, row.Id, now);
            saga.Move(status, now);
            if (status == OrderStatus.PaymentPending) saga.MarkInventoryReserved();
            if (status is OrderStatus.Confirmed or OrderStatus.Cancelled) saga.MarkNotificationRecorded();
            target.Sagas.Add(saga);
        }
        await target.SaveChangesAsync();
        if (Fingerprint(await source.Orders.AsNoTracking().OrderBy(x => x.Id).ToListAsync()) != fingerprint) throw new InvalidOperationException("Source orders changed during import; rolled back.");
        await tx.CommitAsync();
    }
    targetOrders = await target.Orders.AsNoTracking().ToDictionaryAsync(x => x.Id);
    if (targetOrders.Count != sourceOrders.Count || sourceOrders.Any(row => !targetOrders.TryGetValue(row.Id, out var targetRow) || targetRow.CustomerId != row.CustomerId || targetRow.ProductId != row.ProductId || targetRow.Quantity != row.Quantity || targetRow.UnitPrice != row.UnitPrice || targetRow.Status != MapStatus(row.Status) || targetRow.LegacyStatus != row.Status)) throw new InvalidOperationException("Order IDs, snapshots, or statuses diverged.");
    var targetItems = await target.Items.AsNoTracking().ToListAsync();
    if (targetItems.Count != sourceOrders.Count || sourceOrders.Any(row => !targetItems.Any(item => item.OrderId == row.Id && item.ProductId == row.ProductId && item.Quantity == row.Quantity && item.UnitPrice == row.UnitPrice))) throw new InvalidOperationException("Historical order item snapshots diverged.");
    var sagaCount = await target.Sagas.CountAsync();
    if (sagaCount != sourceOrders.Count) throw new InvalidOperationException("Saga row count diverged.");
    Console.WriteLine($"ordersVerified=true source={sourceOrders.Count} target={targetOrders.Count} sagas={sagaCount} items={targetItems.Count} fingerprint={fingerprint} historicalTimestampsAvailable=false");
    return 0;
}
catch (Exception e) { Console.Error.WriteLine($"ordersVerified=false error={e.GetBaseException().Message}"); return 1; }

static OrderStatus MapStatus(string status) => status switch
{
    "PendingPayment" => OrderStatus.PaymentPending,
    "Paid" => OrderStatus.Confirmed,
    "PaymentFailed" => OrderStatus.Cancelled,
    _ => throw new InvalidOperationException($"Unknown historical order status: {status}")
};
static string Fingerprint(IEnumerable<CommerceOrder> orders) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", orders.OrderBy(x => x.Id).Select(x => $"{x.Id:D}|{x.CustomerId:D}|{x.ProductId:D}|{x.Quantity}|{x.UnitPrice}|{x.Status}")))));
