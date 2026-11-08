using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StranglerForge.Catalog.Application;
using StranglerForge.Catalog.Domain;

namespace StranglerForge.Catalog.Infrastructure;

public sealed class CatalogDb(DbContextOptions<CatalogDb> options) : DbContext(options), ICatalogStore
{
    public DbSet<CatalogProduct> Products => Set<CatalogProduct>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CatalogProduct>(entity =>
        {
            entity.ToTable("Products");
            entity.HasKey(product => product.Id);
            entity.Property(product => product.Name).HasMaxLength(200).IsRequired();
            entity.Property(product => product.Price).HasPrecision(18, 2);
            entity.HasIndex(product => product.Name);
        });
    }

    public async Task<IReadOnlyList<CatalogProduct>> ListAsync(CancellationToken cancellationToken) =>
        await Products.AsNoTracking().OrderBy(product => product.Name).ToListAsync(cancellationToken);

    public Task<CatalogProduct?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        Products.AsNoTracking().SingleOrDefaultAsync(product => product.Id == id, cancellationToken);

    public async Task AddAsync(CatalogProduct product, CancellationToken cancellationToken)
    {
        Products.Add(product);
        await SaveChangesAsync(cancellationToken);
    }
}

public static class CatalogInfrastructureRegistration
{
    public static IServiceCollection AddCatalogInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<CatalogDb>(options => options.UseNpgsql(connectionString));
        services.AddScoped<ICatalogStore>(provider => provider.GetRequiredService<CatalogDb>());
        return services;
    }
}
