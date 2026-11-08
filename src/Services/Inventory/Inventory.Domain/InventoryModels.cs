namespace StranglerForge.Inventory.Domain;

public enum ReservationStatus { Reserved, Released, Confirmed }

public sealed class StockItem
{
    public Guid ProductId { get; private set; }
    public int Quantity { get; private set; }
    public int Reserved { get; private set; }
    private StockItem() { }
    public StockItem(Guid productId, int quantity)
    {
        if (productId == Guid.Empty || quantity < 0) throw new ArgumentException("A product and nonnegative stock are required.");
        ProductId = productId;
        Quantity = quantity;
    }
    public int Available => Quantity - Reserved;
    public void Adjust(int delta)
    {
        if ((long)Quantity + delta < Reserved || (long)Quantity + delta > int.MaxValue) throw new InvalidOperationException("Insufficient stock or quantity overflow.");
        Quantity += delta;
    }
    public void Reserve(int quantity)
    {
        if (quantity <= 0 || Available < quantity) throw new InvalidOperationException("Insufficient stock.");
        Reserved += quantity;
    }
    public void Release(int quantity)
    {
        if (quantity <= 0 || Reserved < quantity) throw new InvalidOperationException("Invalid release.");
        Reserved -= quantity;
    }
    public void Confirm(int quantity)
    {
        if (quantity <= 0 || Reserved < quantity) throw new InvalidOperationException("Invalid confirmation.");
        Reserved -= quantity;
        Quantity -= quantity;
    }
}

public sealed class StockReservation
{
    public Guid Id { get; private set; }
    public Guid ProductId { get; private set; }
    public Guid OrderId { get; private set; }
    public int Quantity { get; private set; }
    public ReservationStatus Status { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    private StockReservation() { }
    public StockReservation(Guid id, Guid orderId, Guid productId, int quantity)
    {
        if (id == Guid.Empty || orderId == Guid.Empty || productId == Guid.Empty || quantity <= 0) throw new ArgumentException("Invalid reservation.");
        Id = id; OrderId = orderId; ProductId = productId; Quantity = quantity; Status = ReservationStatus.Reserved; CreatedAtUtc = DateTime.UtcNow;
    }
    public bool Release()
    {
        if (Status == ReservationStatus.Released) return false;
        if (Status == ReservationStatus.Confirmed) throw new InvalidOperationException("Confirmed reservation cannot be released.");
        Status = ReservationStatus.Released; return true;
    }
    public bool Confirm()
    {
        if (Status == ReservationStatus.Confirmed) return false;
        if (Status == ReservationStatus.Released) throw new InvalidOperationException("Released reservation cannot be confirmed.");
        Status = ReservationStatus.Confirmed; return true;
    }
}

public sealed class InventoryAdjustment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public int Delta { get; set; }
    public string Reason { get; set; } = "";
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class InventoryTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public Guid? ReservationId { get; set; }
    public string Kind { get; set; } = "";
    public int Quantity { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
}
