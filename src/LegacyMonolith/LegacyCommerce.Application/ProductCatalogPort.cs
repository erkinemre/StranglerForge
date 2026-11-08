public sealed record ProductSnapshot(Guid Id, decimal Price);

public interface IProductCatalog
{
    Task<ProductSnapshot?> FindAsync(Guid id, CancellationToken cancellationToken);
}

public interface ICorrelationIdAccessor
{
    string? CorrelationId { get; set; }
}

public sealed class CatalogUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
