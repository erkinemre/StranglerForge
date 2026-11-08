using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using StranglerForge.Inventory.Application;
using StranglerForge.Inventory.Domain;

namespace StranglerForge.Inventory.Infrastructure;

public sealed class InventoryDb(DbContextOptions<InventoryDb> options) : DbContext(options)
{
    public DbSet<StockItem> Stock => Set<StockItem>();
    public DbSet<StockReservation> Reservations => Set<StockReservation>();
    public DbSet<InventoryAdjustment> Adjustments => Set<InventoryAdjustment>();
    public DbSet<InventoryTransaction> Transactions => Set<InventoryTransaction>();
    public DbSet<InventoryAuditRecord> Audit => Set<InventoryAuditRecord>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<StockItem>(entity => { entity.ToTable("StockItems", table => table.HasCheckConstraint("CK_StockItems_Balances", "\"Quantity\" >= 0 AND \"Reserved\" >= 0 AND \"Reserved\" <= \"Quantity\"")); entity.HasKey(x => x.ProductId); });
        model.Entity<StockReservation>(entity => { entity.ToTable("StockReservations", table => table.HasCheckConstraint("CK_StockReservations_Quantity", "\"Quantity\" > 0")); entity.HasKey(x => x.Id); entity.HasIndex(x => x.OrderId).IsUnique(); entity.HasIndex(x => x.ProductId); entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32); });
        model.Entity<InventoryAdjustment>(entity => { entity.ToTable("InventoryAdjustments"); entity.HasKey(x => x.Id); entity.Property(x => x.Reason).HasMaxLength(200); });
        model.Entity<InventoryTransaction>(entity => { entity.ToTable("InventoryTransactions"); entity.HasKey(x => x.Id); entity.Property(x => x.Kind).HasMaxLength(32); });
        model.Entity<InventoryAuditRecord>(entity => { entity.ToTable("InventoryAudit"); entity.HasKey(x => x.EventId); entity.HasIndex(x => x.ReservationId); });
        model.AddInboxStateEntity(); model.AddOutboxMessageEntity(); model.AddOutboxStateEntity();
    }

}

public sealed class InventoryAuditRecord
{
    public Guid EventId { get; set; }
    public Guid ReservationId { get; set; }
    public string Kind { get; set; } = "";
    public DateTime OccurredAtUtc { get; set; }
}

public static class InventoryInfrastructureRegistration
{
    public static IServiceCollection AddInventoryInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<InventoryDb>(options => options.UseNpgsql(connectionString));
        services.AddScoped<InventoryStore>();
        services.AddScoped<IInventoryStore>(provider => provider.GetRequiredService<InventoryStore>());
        return services;
    }
}
