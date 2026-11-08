public sealed class CreateProductHandler(ICommerceStore store)
{
    public async Task<UseCaseResult<Product>> CreateProductAsync(string name, decimal price, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || price <= 0)
            return UseCaseResult<Product>.Fail(UseCaseFailure.Invalid, "Name and positive price are required.");
        var product = Product.Create(name, price);
        store.AddProduct(product);
        await store.SaveChangesAsync(ct);
        return UseCaseResult<Product>.Success(product);
    }
}
