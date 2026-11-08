namespace StranglerForge.Orders.Domain;

public enum OrderStatus { Submitted, InventoryPending, InventoryReserved, PaymentPending, PaymentAuthorized, Confirmed, CompensationPending, Cancelled, Failed }

public sealed class Order
{
    private Order() { }
    public Guid Id { get; private set; }
    public Guid CustomerId { get; private set; }
    public Guid ProductId { get; private set; }
    public int Quantity { get; private set; }
    public decimal UnitPrice { get; private set; }
    public decimal Total => UnitPrice * Quantity;
    public OrderStatus Status { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public string? FailureReason { get; private set; }
    public string? LegacyStatus { get; private set; }
    private readonly List<OrderItem> _items = [];
    public IReadOnlyCollection<OrderItem> Items => _items.AsReadOnly();
    public static Order Submit(Guid id, Guid customerId, Guid productId, int quantity, decimal unitPrice, DateTime now)
    {
        if (id == Guid.Empty || customerId == Guid.Empty || productId == Guid.Empty || quantity <= 0 || unitPrice <= 0) throw new ArgumentException("A valid order requires identifiers, positive quantity, and positive unit price.");
        var order = new Order { Id = id, CustomerId = customerId, ProductId = productId, Quantity = quantity, UnitPrice = unitPrice, Status = OrderStatus.Submitted, CreatedAtUtc = now, UpdatedAtUtc = now };
        order._items.Add(OrderItem.Create(id, productId, quantity, unitPrice));
        return order;
    }
    public static Order Import(Guid id, Guid customerId, Guid productId, int quantity, decimal unitPrice, OrderStatus status, string legacyStatus, DateTime now)
    {
        var order = Submit(id, customerId, productId, quantity, unitPrice, now);
        order.Status = status;
        order.LegacyStatus = legacyStatus;
        return order;
    }
    public bool Matches(Guid customerId, Guid productId, int quantity) => CustomerId == customerId && ProductId == productId && Quantity == quantity;
    public void Transition(OrderStatus expected, OrderStatus next, DateTime now, string? reason = null)
    {
        if (Status != expected || !OrderLifecycle.CanTransition(expected, next)) throw new InvalidOperationException($"Cannot transition {Status} to {next}; expected {expected}.");
        Status = next; LegacyStatus = null; UpdatedAtUtc = now; FailureReason = reason;
    }
}

public sealed class OrderSaga
{
    private OrderSaga() { }
    public Guid OrderId { get; private set; }
    public Guid CorrelationId { get; private set; }
    public OrderStatus State { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public int Attempts { get; private set; }
    public string? LastError { get; private set; }
    public bool PaymentRequested { get; private set; }
    public bool? PaymentDecision { get; private set; }
    public bool InventoryReserved { get; private set; }
    public bool InventoryReleased { get; private set; }
    public bool NotificationRecorded { get; private set; }
    public static OrderSaga Start(Guid orderId, Guid correlationId, DateTime now) => new() { OrderId = orderId, CorrelationId = correlationId, State = OrderStatus.Submitted, UpdatedAtUtc = now };
    public void Move(OrderStatus state, DateTime now, string? error = null) { State = state; UpdatedAtUtc = now; LastError = error; Attempts++; }
    public void MarkInventoryReserved() => InventoryReserved = true;
    public void MarkInventoryReleased() => InventoryReleased = true;
    public void MarkPaymentRequested() => PaymentRequested = true;
    public void SetPaymentDecision(bool succeed) => PaymentDecision = succeed;
    public void MarkNotificationRecorded() => NotificationRecorded = true;
}

public sealed class OrderItem
{
    private OrderItem() { }
    public Guid OrderId { get; private set; }
    public Guid ProductId { get; private set; }
    public int Quantity { get; private set; }
    public decimal UnitPrice { get; private set; }
    public decimal Total => UnitPrice * Quantity;
    public static OrderItem Create(Guid orderId, Guid productId, int quantity, decimal unitPrice)
    {
        if (orderId == Guid.Empty || productId == Guid.Empty || quantity <= 0 || unitPrice <= 0) throw new ArgumentException("An order item requires valid identifiers, quantity, and price.");
        return new OrderItem { OrderId = orderId, ProductId = productId, Quantity = quantity, UnitPrice = unitPrice };
    }
}
public static class OrderLifecycle
{
    public static bool CanTransition(OrderStatus from, OrderStatus to) => (from, to) switch
    {
        (OrderStatus.Submitted, OrderStatus.InventoryPending) => true,
        (OrderStatus.InventoryPending, OrderStatus.InventoryReserved or OrderStatus.Failed) => true,
        (OrderStatus.InventoryReserved, OrderStatus.PaymentPending) => true,
        (OrderStatus.PaymentPending, OrderStatus.PaymentAuthorized or OrderStatus.CompensationPending or OrderStatus.PaymentPending) => true,
        (OrderStatus.PaymentAuthorized, OrderStatus.Confirmed) => true,
        (OrderStatus.CompensationPending, OrderStatus.Cancelled) => true,
        _ => false
    };
}
