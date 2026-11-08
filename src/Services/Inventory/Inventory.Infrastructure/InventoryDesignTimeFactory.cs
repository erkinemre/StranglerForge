using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace StranglerForge.Inventory.Infrastructure;

public sealed class InventoryDesignTimeFactory : IDesignTimeDbContextFactory<InventoryDb>
{
    public InventoryDb CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Inventory")
            ?? "Host=127.0.0.1;Database=stranglerforge_inventory;Username=stranglerforge;Password=design-time-only";
        return new InventoryDb(new DbContextOptionsBuilder<InventoryDb>().UseNpgsql(connection).Options);
    }
}
