using System.Security.Cryptography;
using System.Text;

public static class InventoryWriteAuthorization
{
    public static RouteHandlerBuilder RequireInventoryWriteToken(this RouteHandlerBuilder endpoint, string setting) =>
        endpoint.AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
            var environment = context.RequestServices.GetRequiredService<IHostEnvironment>();
            var expected = configuration[setting];

            // Local development retains the existing API workflow; deployed hosts fail closed.
            if (environment.IsDevelopment() && string.IsNullOrWhiteSpace(expected)) return await next(invocation);
            if (string.IsNullOrWhiteSpace(expected)) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            var header = setting == "Inventory:StockAdminToken"
                ? "X-Inventory-Admin-Token"
                : "X-Inventory-Internal-Token";
            var supplied = context.Request.Headers[header].ToString();
            if (string.IsNullOrWhiteSpace(supplied) ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(supplied)))
                return Results.Unauthorized();
            return await next(invocation);
        });
}