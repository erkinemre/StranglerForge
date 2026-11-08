public sealed class DispatchNotificationHandler(ICommerceStore store)
{
    public Task<bool> DispatchNotificationAsync(Guid id, CancellationToken ct) => store.DispatchNotificationAsync(id, ct);
}
