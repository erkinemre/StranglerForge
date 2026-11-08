namespace StranglerForge.Inventory.Application;

public sealed record InventoryReservedV1(Guid EventId, Guid CorrelationId, DateTime OccurredAtUtc, int EventVersion, Guid ReservationId, Guid OrderId, Guid ProductId, int Quantity);
public sealed record InventoryReservationRejectedV1(Guid EventId, Guid CorrelationId, DateTime OccurredAtUtc, int EventVersion, Guid ReservationId, Guid OrderId, Guid ProductId, int Quantity, string Reason);
public sealed record InventoryReservationReleasedV1(Guid EventId, Guid CorrelationId, DateTime OccurredAtUtc, int EventVersion, Guid ReservationId, Guid OrderId, Guid ProductId, int Quantity);
public sealed record InventoryReservationConfirmedV1(Guid EventId, Guid CorrelationId, DateTime OccurredAtUtc, int EventVersion, Guid ReservationId, Guid OrderId, Guid ProductId, int Quantity);
public sealed record InventoryAdjustedV1(Guid EventId, Guid CorrelationId, DateTime OccurredAtUtc, int EventVersion, Guid ProductId, int Delta, int Available);
