public sealed class ExternalOrderBoundary(ICommerceStore store)
{
    public async Task<(PaymentAttempt? Payment, string? Error)> AuthorizeAsync(Guid orderId, decimal amount, bool succeed, CancellationToken ct)
    {
        if (orderId == Guid.Empty || amount <= 0) return (null, "Invalid payment authorization.");
        var prior = await store.GetPaymentAsync(orderId, ct);
        if (prior is not null) return prior.Amount == amount && prior.Succeeded == succeed ? (prior, null) : (null, "Payment decision conflicts with an earlier authorization.");
        var payment = new PaymentAttempt { Id = orderId, OrderId = orderId, Amount = amount, Succeeded = succeed };
        try
        {
            store.AddPayment(payment);
            await store.SaveChangesAsync(ct);
            return (payment, null);
        }
        catch (Exception)
        {
            var committed = await store.GetPaymentAsync(orderId, ct);
            if (committed is not null && committed.Amount == amount && committed.Succeeded == succeed) return (committed, null);
            throw;
        }
    }
    public async Task<bool> RecordNotificationAsync(Guid orderId, Guid customerId, bool succeeded, CancellationToken ct)
    {
        if (orderId == Guid.Empty || customerId == Guid.Empty) return false;
        if (await store.GetNotificationAsync(orderId, ct) is not null) return true;
        store.AddNotification(new NotificationRequest { Id = orderId, CustomerId = customerId, Message = succeeded ? "Order paid" : "Order failed", Status = "Pending" });
        try { await store.SaveChangesAsync(ct); return true; }
        catch (Exception) { return await store.GetNotificationAsync(orderId, ct) is not null; }
    }
}
