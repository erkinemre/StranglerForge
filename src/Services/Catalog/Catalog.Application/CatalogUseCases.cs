using StranglerForge.Catalog.Domain;

namespace StranglerForge.Catalog.Application;

public interface ICatalogStore
{
    Task<IReadOnlyList<CatalogProduct>> ListAsync(CancellationToken cancellationToken);
    Task<CatalogProduct?> FindAsync(Guid id, CancellationToken cancellationToken);
    Task AddAsync(CatalogProduct product, CancellationToken cancellationToken);
}

public sealed class CreateProductHandler(ICatalogStore store)
{
    public async Task<CatalogProduct> HandleAsync(string name, decimal price, CancellationToken cancellationToken)
    {
        var product = CatalogProduct.Create(Guid.NewGuid(), name, price);
        await store.AddAsync(product, cancellationToken);
        return product;
    }
}

public sealed class ProductQueries(ICatalogStore store)
{
    public Task<IReadOnlyList<CatalogProduct>> ListAsync(CancellationToken cancellationToken) =>
        store.ListAsync(cancellationToken);

    public Task<CatalogProduct?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        store.FindAsync(id, cancellationToken);
}
