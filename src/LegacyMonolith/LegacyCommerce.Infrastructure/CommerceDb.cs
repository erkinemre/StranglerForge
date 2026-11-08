using Microsoft.EntityFrameworkCore;

public sealed class CommerceDb(DbContextOptions<CommerceDb> options) : DbContext(options), ICommerceStore
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<StockItem> Stock => Set<StockItem>();
    public DbSet<CommerceOrder> Orders => Set<CommerceOrder>();
    public DbSet<PaymentAttempt> Payments => Set<PaymentAttempt>();
    public DbSet<NotificationRequest> Notifications => Set<NotificationRequest>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Customer>().HasIndex(x => x.Email).IsUnique();
        model.Entity<Customer>().Property(x => x.Name).HasMaxLength(200);
        model.Entity<Customer>().Property(x => x.Email).HasMaxLength(320);
        model.Entity<Product>().Property(x => x.Name).HasMaxLength(200);
        model.Entity<Product>().Property(x => x.Price).HasPrecision(18, 2);
        model.Entity<StockItem>().HasKey(x => x.ProductId);
        model.Entity<CommerceOrder>().Property(x => x.UnitPrice).HasPrecision(18, 2);
        model.Entity<CommerceOrder>().Property(x => x.Status).HasMaxLength(40);
        model.Entity<PaymentAttempt>().Property(x => x.Amount).HasPrecision(18, 2);
        model.Entity<PaymentAttempt>().HasIndex(x => x.OrderId).IsUnique();
        model.Entity<NotificationRequest>().Property(x => x.Message).HasMaxLength(500);
        model.Entity<NotificationRequest>().Property(x => x.Status).HasMaxLength(40);
        model.Entity<Customer>().HasData(new Customer { Id = Guid.Parse("00000000-0000-0000-0000-000000000101"), Name = "Sample Customer", Email = "sample@example.test" });
        model.Entity<Product>().HasData(new Product { Id = Guid.Parse("00000000-0000-0000-0000-000000000201"), Name = "Sample Product", Price = 19.99m });
        model.Entity<StockItem>().HasData(new StockItem { ProductId = Guid.Parse("00000000-0000-0000-0000-000000000201"), Quantity = 25 });
    }

    public async Task<ICommerceTransaction> BeginTransactionAsync(CancellationToken ct) =>
        new CommerceTransaction(await Database.BeginTransactionAsync(ct));
    public Task<bool> CustomerEmailExistsAsync(string email, CancellationToken ct) =>
        Customers.AnyAsync(x => x.Email == email, ct);
    public Task<bool> CustomerExistsAsync(Guid id, CancellationToken ct) =>
        Customers.AnyAsync(x => x.Id == id, ct);
    public Task<Customer?> GetCustomerAsync(Guid id, CancellationToken ct) =>
        Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    public Task<Product?> GetProductAsync(Guid id, CancellationToken ct) =>
        Products.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    public async Task<IReadOnlyList<Product>> GetProductsAsync(CancellationToken ct) =>
        await Products.AsNoTracking().ToListAsync(ct);
    public Task<StockItem?> GetStockAsync(Guid id, CancellationToken ct) =>
        Stock.AsNoTracking().SingleOrDefaultAsync(x => x.ProductId == id, ct);
    public Task<CommerceOrder?> GetOrderAsync(Guid id, CancellationToken ct) =>
        Orders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    public Task<PaymentAttempt?> GetPaymentAsync(Guid orderId, CancellationToken ct) => Payments.AsNoTracking().SingleOrDefaultAsync(x => x.OrderId == orderId, ct);
    public Task<NotificationRequest?> GetNotificationAsync(Guid id, CancellationToken ct) => Notifications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    public async Task<IReadOnlyList<NotificationRequest>> GetNotificationsAsync(Guid id, CancellationToken ct) =>
        await Notifications.AsNoTracking().Where(x => x.CustomerId == id).ToListAsync(ct);
    public async Task<int> AddStockAsync(Guid productId, int quantity, CancellationToken ct)
    {
        var changed = await Stock.Where(x => x.ProductId == productId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Quantity, x => x.Quantity + quantity), ct);
        if (changed == 0)
        {
            Stock.Add(new StockItem { ProductId = productId, Quantity = quantity });
            await SaveChangesAsync(ct);
            return quantity;
        }
        return (await GetStockAsync(productId, ct))!.Quantity;
    }
    public async Task<bool> ReserveStockAsync(Guid productId, int quantity, CancellationToken ct) =>
        await Stock.Where(x => x.ProductId == productId && x.Quantity >= quantity)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Quantity, x => x.Quantity - quantity), ct) == 1;
    public async Task ReleaseStockAsync(Guid productId, int quantity, CancellationToken ct) =>
        _ = await Stock.Where(x => x.ProductId == productId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Quantity, x => x.Quantity + quantity), ct);
    public async Task<bool> TrySetOrderStatusAsync(Guid orderId, string status, CancellationToken ct) =>
        await Orders.Where(x => x.Id == orderId && x.Status == OrderStatuses.PendingPayment)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status), ct) == 1;
    public async Task<bool> DispatchNotificationAsync(Guid id, CancellationToken ct) =>
        await Notifications.Where(x => x.Id == id && x.Status == "Pending")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Sent"), ct) == 1;
    public void AddCustomer(Customer customer) => Customers.Add(customer);
    public void AddProduct(Product product) => Products.Add(product);
    public void AddOrder(CommerceOrder order) => Orders.Add(order);
    public void AddPayment(PaymentAttempt payment) => Payments.Add(payment);
    public void AddNotification(NotificationRequest notification) => Notifications.Add(notification);
    Task ICommerceStore.SaveChangesAsync(CancellationToken ct) => base.SaveChangesAsync(ct);

    private sealed class CommerceTransaction(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction) : ICommerceTransaction
    {
        public Task CommitAsync(CancellationToken ct) => transaction.CommitAsync(ct);
        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }

}
