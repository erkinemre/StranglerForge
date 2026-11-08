public sealed class AddStockHandler(IProductCatalog catalog, IInventoryGateway inventory)
{
    public async Task<UseCaseResult<int>> AddStockAsync(Guid productId, int quantity, CancellationToken ct)
    {
        if (quantity <= 0) return UseCaseResult<int>.Fail(UseCaseFailure.Invalid, "Quantity must be positive.");
        if (await catalog.FindAsync(productId, ct) is null) return UseCaseResult<int>.Fail(UseCaseFailure.Missing, "Product does not exist.");
        return UseCaseResult<int>.Success(await inventory.AddStockAsync(productId, quantity, ct));
    }
}
