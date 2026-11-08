public static class OrdersEndpoints
{
    public static void MapOrdersEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/orders", async (OrderInput input, CreateOrderHandler commands, IConfiguration config, HttpContext context, CancellationToken ct) =>
        {
            if (config["OrdersOwnership:Mode"] == "Extracted") return Results.StatusCode(410);
            var supplied = context.Request.Headers["Idempotency-Key"].ToString();
            if (supplied.Length > 0 && !Guid.TryParse(supplied, out _)) return Results.BadRequest("Idempotency-Key must be a GUID.");
            var orderId = supplied.Length > 0 ? Guid.Parse(supplied) : (Guid?)null;
            var result = await commands.CreateOrderAsync(input.CustomerId, input.ProductId, input.Quantity, ct, orderId);
            return result.Failure switch
            {
                UseCaseFailure.Invalid => Results.BadRequest(result.Message),
                UseCaseFailure.Conflict => Results.Conflict(result.Message),
                _ => Results.Created($"/api/orders/{result.Value!.Id}", result.Value)
            };
        });
        app.MapGet("/api/orders/{id:guid}", async (Guid id, CommerceQueries queries, CancellationToken ct) =>
            await queries.GetOrderAsync(id, ct) is { } order ? Results.Ok(order) : Results.NotFound());
    }
}
