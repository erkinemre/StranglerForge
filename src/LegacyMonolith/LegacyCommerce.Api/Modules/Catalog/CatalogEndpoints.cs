public static class CatalogEndpoints
{
    public static void MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/products", async (ProductInput input, CreateProductHandler commands,
            IConfiguration configuration, CancellationToken ct) =>
        {
            if (configuration["CatalogOwnership:Mode"] != "Legacy")
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            var result = await commands.CreateProductAsync(input.Name, input.Price, ct);
            return result.Failure == UseCaseFailure.Invalid
                ? Results.BadRequest(result.Message)
                : Results.Created($"/api/products/{result.Value!.Id}", result.Value);
        });
        app.MapGet("/api/products", async (CommerceQueries queries, IConfiguration configuration, CancellationToken ct) =>
            configuration["CatalogOwnership:Mode"] == "Extracted"
                ? Results.StatusCode(StatusCodes.Status410Gone)
                : Results.Ok(await queries.GetProductsAsync(ct)));
    }
}
