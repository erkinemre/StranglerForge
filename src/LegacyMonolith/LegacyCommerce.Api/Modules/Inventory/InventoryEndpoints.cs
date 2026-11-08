public static class InventoryEndpoints
{
    public static void MapInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        if ((app.ServiceProvider.GetRequiredService<IConfiguration>()["InventoryOwnership:Mode"] ?? "Legacy") == "Extracted") return;

        app.MapPost("/api/inventory/{productId:guid}/stock", async (Guid productId, StockInput input, AddStockHandler commands, CancellationToken ct) =>
        {
            var result = await commands.AddStockAsync(productId, input.Quantity, ct);
            return result.Failure switch
            {
                UseCaseFailure.Invalid => Results.BadRequest(result.Message),
                UseCaseFailure.Missing => Results.NotFound(),
                _ => Results.Ok(new { productId, quantity = result.Value })
            };
        });
        app.MapGet("/api/inventory/{productId:guid}", async (Guid productId, CommerceQueries queries, CancellationToken ct) =>
            await queries.GetStockAsync(productId, ct) is { } stock ? Results.Ok(stock) : Results.NotFound());
    }
}
