public record CustomerInput(string Name, string Email);
public record ProductInput(string Name, decimal Price);
public record StockInput(int Quantity);
public record OrderInput(Guid CustomerId, Guid ProductId, int Quantity);
public record PaymentInput(bool Succeed);
