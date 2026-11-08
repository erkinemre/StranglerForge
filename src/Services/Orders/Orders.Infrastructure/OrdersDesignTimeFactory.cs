using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace StranglerForge.Orders.Infrastructure;

public sealed class OrdersDesignTimeFactory : IDesignTimeDbContextFactory<OrdersDb>
{
    public OrdersDb CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Orders") ?? "Host=localhost;Database=stranglerforge_orders;Username=stranglerforge;Password=design-time-only";
        return new OrdersDb(new DbContextOptionsBuilder<OrdersDb>().UseNpgsql(connection).Options);
    }
}
