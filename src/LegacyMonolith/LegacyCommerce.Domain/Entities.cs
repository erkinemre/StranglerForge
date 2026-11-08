public sealed class Customer
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Email { get; set; }

    public static Customer Create(string name, string normalizedEmail) =>
        new() { Id = Guid.NewGuid(), Name = name.Trim(), Email = normalizedEmail };
}

public sealed class Product
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public decimal Price { get; set; }

    public static Product Create(string name, decimal price)
    {
        if (string.IsNullOrWhiteSpace(name) || price <= 0)
            throw new ArgumentException("A product requires a name and positive price.");
        return new Product { Id = Guid.NewGuid(), Name = name.Trim(), Price = price };
    }
}

public sealed class StockItem
{
    public Guid ProductId { get; set; }
    public int Quantity { get; set; }
}

public sealed class CommerceOrder
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Guid ProductId { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public required string Status { get; set; }

    public static CommerceOrder Create(Guid customerId, Guid productId, decimal unitPrice, int quantity, Guid? orderId = null)
    {
        if (quantity <= 0 || unitPrice <= 0 || orderId == Guid.Empty)
            throw new ArgumentException("An order requires a positive quantity and unit price.");
        return new CommerceOrder { Id = orderId ?? Guid.NewGuid(), CustomerId = customerId,
            ProductId = productId, Quantity = quantity, UnitPrice = unitPrice,
            Status = OrderStatuses.PendingPayment };
    }

    public static string StatusAfterPayment(bool succeeded) =>
        succeeded ? OrderStatuses.Paid : OrderStatuses.PaymentFailed;
}

public sealed class PaymentAttempt
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public decimal Amount { get; set; }
    public bool Succeeded { get; set; }

    public static PaymentAttempt ForOrder(CommerceOrder order, bool succeeded) =>
        new() { Id = Guid.NewGuid(), OrderId = order.Id,
            Amount = order.UnitPrice * order.Quantity, Succeeded = succeeded };
}

public sealed class NotificationRequest
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public required string Message { get; set; }
    public required string Status { get; set; }

    public static NotificationRequest ForPayment(CommerceOrder order, bool succeeded) =>
        new() { Id = Guid.NewGuid(), CustomerId = order.CustomerId,
            Message = succeeded ? "Order paid" : "Payment failed", Status = "Pending" };
}
