public sealed class ProcessPaymentHandler(ICommerceStore store, IInventoryGateway inventory)
{
    public async Task<UseCaseResult<PaymentAttempt>> ProcessPaymentAsync(Guid orderId, bool succeed, CancellationToken ct)
    {
        var order = await store.GetOrderAsync(orderId, ct);
        if (order is null) return UseCaseResult<PaymentAttempt>.Fail(UseCaseFailure.Missing, "Order does not exist.");
        if (order.Status != OrderStatuses.PendingPayment)
        {
            if (order.Status is OrderStatuses.Paid or OrderStatuses.PaymentFailed)
                await FinalizeInventoryAsync(order, order.Status == OrderStatuses.Paid, ct);
            return UseCaseResult<PaymentAttempt>.Fail(UseCaseFailure.Conflict, "Order already processed.");
        }
        await using (var transaction = await store.BeginTransactionAsync(ct))
        {
            if (!await store.TrySetOrderStatusAsync(orderId, CommerceOrder.StatusAfterPayment(succeed), ct))
                return UseCaseResult<PaymentAttempt>.Fail(UseCaseFailure.Conflict, "Order already processed.");
            var payment = PaymentAttempt.ForOrder(order, succeed);
            store.AddPayment(payment);
            store.AddNotification(NotificationRequest.ForPayment(order, succeed));
            await store.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            await FinalizeInventoryAsync(order, succeed, ct);
            return UseCaseResult<PaymentAttempt>.Success(payment);
        }
    }

    private Task FinalizeInventoryAsync(CommerceOrder order, bool succeeded, CancellationToken ct) =>
        succeeded ? inventory.ConfirmAsync(order.Id, ct) : inventory.ReleaseAsync(order.Id, order.ProductId, order.Quantity, ct);
}
