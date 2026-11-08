public sealed class CreateOrderHandler(ICommerceStore store, IProductCatalog catalog, IInventoryGateway inventory)
{
    public async Task<UseCaseResult<CommerceOrder>> CreateOrderAsync(Guid customerId, Guid productId, int quantity, CancellationToken ct, Guid? requestedOrderId = null)
    {
        if (quantity <= 0 || requestedOrderId == Guid.Empty)
            return UseCaseResult<CommerceOrder>.Fail(UseCaseFailure.Invalid, "Quantity and order identifier must be valid.");
        if (requestedOrderId is { } stableId && await store.GetOrderAsync(stableId, ct) is { } previous)
            return Matches(previous, customerId, productId, quantity)
                ? UseCaseResult<CommerceOrder>.Success(previous)
                : UseCaseResult<CommerceOrder>.Fail(UseCaseFailure.Conflict, "Idempotency key belongs to another order.");
        if (!await store.CustomerExistsAsync(customerId, ct)) return UseCaseResult<CommerceOrder>.Fail(UseCaseFailure.Invalid, "Customer does not exist.");
        var product = await catalog.FindAsync(productId, ct);
        if (product is null) return UseCaseResult<CommerceOrder>.Fail(UseCaseFailure.Invalid, "Product does not exist.");
        var order = CommerceOrder.Create(customerId, product.Id, product.Price, quantity, requestedOrderId);
        if (!await inventory.ReserveAsync(order.Id, order.Id, productId, quantity, ct))
            return UseCaseResult<CommerceOrder>.Fail(UseCaseFailure.Conflict, "Insufficient stock.");
        try
        {
            await using var transaction = await store.BeginTransactionAsync(ct);
            store.AddOrder(order);
            await store.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return UseCaseResult<CommerceOrder>.Success(order);
        }
        catch
        {
            if (await store.GetOrderAsync(order.Id, CancellationToken.None) is { } committed && Matches(committed, customerId, productId, quantity))
                return UseCaseResult<CommerceOrder>.Success(committed);
            await inventory.ReleaseAsync(order.Id, productId, quantity, CancellationToken.None);
            throw;
        }
    }

    private static bool Matches(CommerceOrder order, Guid customerId, Guid productId, int quantity) =>
        order.CustomerId == customerId && order.ProductId == productId && order.Quantity == quantity;
}
