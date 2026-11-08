using Npgsql;

namespace LegacyCommerce.IntegrationTests;

public static class CatalogTestDatabase
{
    public static async Task WaitUntilAvailableAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        string? lastFailure = null;
        const int maxAttempts = 20;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                await connection.OpenAsync(attemptTimeout.Token);
                return;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
                exception is NpgsqlException or TimeoutException or OperationCanceledException)
            {
                lastFailure = $"{exception.GetType().Name}: {exception.Message}";
                if (attempt < maxAttempts)
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"PostgreSQL test database {settings.Host}:{settings.Port}/{settings.Database} was unavailable after {maxAttempts} attempts. Last failure: {lastFailure}");
    }
}
