public static class CustomersEndpoints
{
    public static void MapCustomersEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/customers", async (CustomerInput input, CreateCustomerHandler commands, CancellationToken ct) =>
        {
            var result = await commands.CreateCustomerAsync(input.Name, input.Email, ct);
            return result.Failure switch
            {
                UseCaseFailure.Invalid => Results.BadRequest(result.Message),
                UseCaseFailure.Conflict => Results.Conflict(result.Message),
                _ => Results.Created($"/api/customers/{result.Value!.Id}", result.Value)
            };
        });
        app.MapGet("/api/customers/{id:guid}", async (Guid id, CommerceQueries queries, CancellationToken ct) =>
            await queries.GetCustomerAsync(id, ct) is { } customer ? Results.Ok(customer) : Results.NotFound());
    }
}
