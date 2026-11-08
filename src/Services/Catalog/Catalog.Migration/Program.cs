using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using StranglerForge.Catalog.Domain;
using StranglerForge.Catalog.Infrastructure;

var mode = args.SingleOrDefault();
if (mode is not "--import" and not "--verify" and not "--append-missing")
{
    Console.Error.WriteLine("Usage: Catalog.Migration --import|--verify|--append-missing");
    return 2;
}
if ((mode == "--import" || mode == "--append-missing") && Environment.GetEnvironmentVariable("CATALOG_WRITE_FREEZE_ACK") != "true")
{
    Console.Error.WriteLine("Import requires CATALOG_WRITE_FREEZE_ACK=true after freezing legacy Catalog writes.");
    return 2;
}
var sourceConnection = Environment.GetEnvironmentVariable("ConnectionStrings__LegacyCommerce");
var targetConnection = Environment.GetEnvironmentVariable("ConnectionStrings__Catalog");
if (string.IsNullOrWhiteSpace(sourceConnection) || string.IsNullOrWhiteSpace(targetConnection))
{
    Console.Error.WriteLine("Both ConnectionStrings__LegacyCommerce and ConnectionStrings__Catalog are required.");
    return 2;
}

try
{
    await using var source = new CommerceDb(new DbContextOptionsBuilder<CommerceDb>()
        .UseSqlServer(sourceConnection).Options);
    await using var target = new CatalogDb(new DbContextOptionsBuilder<CatalogDb>()
        .UseNpgsql(targetConnection).Options);
    if (mode == "--import") await target.Database.MigrateAsync();
    var products = await source.Products.AsNoTracking().OrderBy(product => product.Id).ToListAsync();
    var stockIds = await source.Stock.AsNoTracking().Select(stock => stock.ProductId).Distinct().ToListAsync();
    var orderIds = await source.Orders.AsNoTracking().Select(order => order.ProductId).Distinct().ToListAsync();
    var sourceIds = products.Select(product => product.Id).ToHashSet();
    var targetProducts = await target.Products.AsNoTracking().ToDictionaryAsync(product => product.Id);
    var missingReferences = stockIds.Concat(orderIds).Distinct().Where(id => !sourceIds.Contains(id) && (mode != "--append-missing" || !targetProducts.ContainsKey(id))).ToList();
    Console.WriteLine($"sourceCount={products.Count} stockReferences={stockIds.Count} orderReferences={orderIds.Count} duplicateNames={products.GroupBy(product => product.Name).Count(group => group.Count() > 1)}");
    if (missingReferences.Count > 0)
        throw new InvalidOperationException($"{missingReferences.Count} inventory/order product references are absent from source Catalog.");

    var extraIds = targetProducts.Keys.Where(id => !sourceIds.Contains(id)).ToList();
    if (mode != "--append-missing" && extraIds.Count > 0)
        throw new InvalidOperationException($"Target contains {extraIds.Count} IDs absent from source; refusing to overwrite post-cutover data.");

    if (mode == "--append-missing")
    {
        var referencedIds = stockIds.Concat(orderIds).ToHashSet();
        var append = products.Where(product => referencedIds.Contains(product.Id) && !targetProducts.ContainsKey(product.Id)).ToList();
        await using var transaction = await target.Database.BeginTransactionAsync();
        foreach (var product in append) target.Products.Add(CatalogProduct.Create(product.Id, product.Name, product.Price));
        await target.SaveChangesAsync();
        var sourceAfter = await source.Products.AsNoTracking().OrderBy(product => product.Id).ToListAsync();
        if (Fingerprint(products.Select(product => (product.Id, product.Name, product.Price))) !=
            Fingerprint(sourceAfter.Select(product => (product.Id, product.Name, product.Price))))
            throw new InvalidOperationException("Source Catalog changed during append; target transaction rolled back.");
        var targetAfter = await target.Products.AsNoTracking().ToDictionaryAsync(product => product.Id);
        if (append.Any(product => !targetAfter.TryGetValue(product.Id, out var row) || row.Name != product.Name || row.Price != product.Price) ||
            referencedIds.Any(id => !targetAfter.ContainsKey(id)))
            throw new InvalidOperationException("Referenced Catalog identifiers did not reconcile; target transaction rolled back.");
        await transaction.CommitAsync();
        Console.WriteLine($"appended={append.Count} referencedIds={referencedIds.Count} preservedExisting={targetProducts.Count}");
    }
    else if (mode == "--import")
    {
        await using var transaction = await target.Database.BeginTransactionAsync();
        var created = 0;
        var updated = 0;
        var unchanged = 0;
        foreach (var product in products)
        {
            if (!targetProducts.TryGetValue(product.Id, out var existing))
            {
                target.Products.Add(CatalogProduct.Create(product.Id, product.Name, product.Price));
                created++;
            }
            else if (existing.Name != product.Name || existing.Price != product.Price)
            {
                await target.Products.Where(row => row.Id == product.Id)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(row => row.Name, product.Name)
                        .SetProperty(row => row.Price, product.Price));
                updated++;
            }
            else unchanged++;
        }
        await target.SaveChangesAsync();
        var sourceAfter = await source.Products.AsNoTracking().OrderBy(product => product.Id).ToListAsync();
        if (Fingerprint(products.Select(product => (product.Id, product.Name, product.Price))) !=
            Fingerprint(sourceAfter.Select(product => (product.Id, product.Name, product.Price))))
            throw new InvalidOperationException("Source Catalog changed during migration; target transaction rolled back.");
        var targetAfter = await target.Products.AsNoTracking().OrderBy(product => product.Id).ToListAsync();
        Verify(products.Select(product => (product.Id, product.Name, product.Price)),
            targetAfter.Select(product => (product.Id, product.Name, product.Price)));
        await transaction.CommitAsync();
        Console.WriteLine($"created={created} updated={updated} unchanged={unchanged}");
    }
    else
    {
        Verify(products.Select(product => (product.Id, product.Name, product.Price)),
            targetProducts.Values.Select(product => (product.Id, product.Name, product.Price)));
    }
    Console.WriteLine($"cutoverReady=true count={products.Count} fingerprint={Fingerprint(products.Select(product => (product.Id, product.Name, product.Price)))}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"cutoverReady=false error={exception.Message}");
    return 1;
}

static void Verify(IEnumerable<(Guid Id, string Name, decimal Price)> source,
    IEnumerable<(Guid Id, string Name, decimal Price)> target)
{
    var sourceList = source.ToList();
    var targetList = target.ToList();
    if (sourceList.Count != targetList.Count || Fingerprint(sourceList) != Fingerprint(targetList))
        throw new InvalidOperationException($"Catalog mismatch: source={sourceList.Count}, target={targetList.Count}.");
}

static string Fingerprint(IEnumerable<(Guid Id, string Name, decimal Price)> products)
{
    var lines = products.OrderBy(product => product.Id)
        .Select(product => $"{product.Id:D}|{product.Name}|{product.Price.ToString("0.00", CultureInfo.InvariantCulture)}");
    var bytes = Encoding.UTF8.GetBytes(string.Join("\n", lines));
    return Convert.ToHexString(SHA256.HashData(bytes));
}
