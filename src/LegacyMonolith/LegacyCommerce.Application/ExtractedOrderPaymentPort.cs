public interface IExtractedOrderPayment
{
    Task<(int StatusCode, string? Body)> SubmitIntentAsync(Guid orderId, bool succeed, string? correlationId, CancellationToken ct);
}
