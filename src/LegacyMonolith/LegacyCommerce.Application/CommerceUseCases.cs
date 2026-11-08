
public enum UseCaseFailure { None, Invalid, Missing, Conflict }
public sealed record UseCaseResult<T>(T? Value, UseCaseFailure Failure, string? Message = null)
{
    public static UseCaseResult<T> Success(T value) => new(value, UseCaseFailure.None);
    public static UseCaseResult<T> Fail(UseCaseFailure failure, string message) => new(default, failure, message);
}

public interface ICommerceTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}

public interface ICommerceStore
{
    Task<ICommerceTransaction> BeginTransactionAsync(CancellationToken cancellationToken);
    Task<bool> CustomerEmailExistsAsync(string email, CancellationToken cancellationToken);
    Task<bool> CustomerExistsAsync(Guid id, CancellationToken cancellationToken);
    Task<Customer?> GetCustomerAsync(Guid id, CancellationToken cancellationToken);
    Task<Product?> GetProductAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<Product>> GetProductsAsync(CancellationToken cancellationToken);
    Task<StockItem?> GetStockAsync(Guid productId, CancellationToken cancellationToken);
    Task<CommerceOrder?> GetOrderAsync(Guid id, CancellationToken cancellationToken);
    Task<PaymentAttempt?> GetPaymentAsync(Guid orderId, CancellationToken cancellationToken);
    Task<NotificationRequest?> GetNotificationAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<NotificationRequest>> GetNotificationsAsync(Guid customerId, CancellationToken cancellationToken);
    Task<int> AddStockAsync(Guid productId, int quantity, CancellationToken cancellationToken);
    Task<bool> ReserveStockAsync(Guid productId, int quantity, CancellationToken cancellationToken);
    Task ReleaseStockAsync(Guid productId, int quantity, CancellationToken cancellationToken);
    Task<bool> TrySetOrderStatusAsync(Guid orderId, string status, CancellationToken cancellationToken);
    Task<bool> DispatchNotificationAsync(Guid id, CancellationToken cancellationToken);
    void AddCustomer(Customer customer);
    void AddProduct(Product product);
    void AddOrder(CommerceOrder order);
    void AddPayment(PaymentAttempt payment);
    void AddNotification(NotificationRequest notification);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public sealed class CommerceQueries(ICommerceStore store, IInventoryGateway inventory)
{
    public Task<Customer?> GetCustomerAsync(Guid id, CancellationToken ct) => store.GetCustomerAsync(id, ct);
    public Task<Product?> GetProductAsync(Guid id, CancellationToken ct) => store.GetProductAsync(id, ct);
    public Task<IReadOnlyList<Product>> GetProductsAsync(CancellationToken ct) => store.GetProductsAsync(ct);
    public async Task<StockItem?> GetStockAsync(Guid id, CancellationToken ct) => await inventory.GetStockAsync(id, ct) is { } stock ? new StockItem { ProductId = id, Quantity = stock.Quantity } : null;
    public Task<CommerceOrder?> GetOrderAsync(Guid id, CancellationToken ct) => store.GetOrderAsync(id, ct);
    public Task<IReadOnlyList<NotificationRequest>> GetNotificationsAsync(Guid id, CancellationToken ct) => store.GetNotificationsAsync(id, ct);
}
