public static class NotificationsEndpoints
{
    public static void MapNotificationsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/notifications/{customerId:guid}", (Guid customerId, CommerceQueries queries, CancellationToken ct) =>
            queries.GetNotificationsAsync(customerId, ct));
        app.MapPost("/api/notifications/{id:guid}/dispatch", async (Guid id, DispatchNotificationHandler commands, CancellationToken ct) =>
            await commands.DispatchNotificationAsync(id, ct) ? Results.NoContent() : Results.NotFound());
    }
}
