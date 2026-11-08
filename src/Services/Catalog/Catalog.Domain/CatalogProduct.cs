namespace StranglerForge.Catalog.Domain;

public sealed class CatalogProduct
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public decimal Price { get; private set; }

    private CatalogProduct() { }

    public static CatalogProduct Create(Guid id, string name, decimal price)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(name) || price <= 0)
            throw new ArgumentException("A product requires an identifier, name, and positive price.");
        return new CatalogProduct { Id = id, Name = name.Trim(), Price = price };
    }
}
