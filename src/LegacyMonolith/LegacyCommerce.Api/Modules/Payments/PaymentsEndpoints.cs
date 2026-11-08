public static class PaymentsEndpoints
{
    public static void MapPaymentsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/orders/{id:guid}/payment", async (Guid id, PaymentInput input, ProcessPaymentHandler commands, IConfiguration config, IServiceProvider services, HttpContext context, CancellationToken ct) =>
        {
            if (config["OrdersOwnership:Mode"] == "Extracted")
            {
                var forwarded = await services.GetRequiredService<IExtractedOrderPayment>().SubmitIntentAsync(id, input.Succeed, context.Request.Headers["X-Correlation-ID"].ToString(), ct);
                return forwarded.StatusCode switch
                {
                    202 => Results.Content(forwarded.Body, "application/json", statusCode: 202),
                    404 => Results.NotFound(),
                    409 => Results.Conflict(forwarded.Body),
                    _ => Results.StatusCode(503)
                };
            }
            var result = await commands.ProcessPaymentAsync(id, input.Succeed, ct);
            return result.Failure switch
            {
                UseCaseFailure.Missing => Results.NotFound(),
                UseCaseFailure.Conflict => Results.Conflict(result.Message),
                _ => Results.Ok(result.Value)
            };
        });
    }
}
