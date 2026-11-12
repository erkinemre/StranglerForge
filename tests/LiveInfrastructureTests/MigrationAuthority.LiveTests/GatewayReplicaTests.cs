using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using StranglerForge.Gateway.MigrationControl;
using Xunit;

namespace MigrationAuthority.LiveTests;

public sealed class GatewayReplicaTests
{
    [Fact(Timeout = 90_000)]
    public async Task ReplicasRejectSharedTransitionAndStaleOrUnavailableAuthority()
    {
        var connection = Environment.GetEnvironmentVariable("MIGRATION_TEST_CONNECTION")
            ?? throw new InvalidOperationException("MIGRATION_TEST_CONNECTION must identify an isolated PostgreSQL test database.");
        var schema = "gateway_replica_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var settings = new NpgsqlConnectionStringBuilder(connection) { SearchPath = schema, Pooling = false };
            await using (var first = CreateGateway(settings.ConnectionString))
            await using (var second = CreateGateway(settings.ConnectionString))
            {
                using var firstClient = first.CreateClient();
                using var secondClient = second.CreateClient();
                Assert.Equal(HttpStatusCode.OK, (await firstClient.GetAsync("/health/ready")).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await secondClient.GetAsync("/health/ready")).StatusCode);

                var states = first.Services.GetRequiredService<MigrationStateStore>();
                var flags = MigrationFlagReader.Read(first.Services.GetRequiredService<IConfiguration>());
                Assert.NotNull(first.Services.GetService<PostgresMigrationAuthority>());
                Assert.Equal(0, states.Get(MigrationService.Catalog).Version);
                var transition = await states.TransitionAsync(
                    MigrationService.Catalog, new TransitionRequest(MigrationStage.RollbackPending, 0),
                    flags, "test", Guid.NewGuid().ToString("D"), CancellationToken.None);
                Assert.True(transition.Succeeded, transition.Error);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, (await secondClient.PostAsJsonAsync("/api/products", new { name = "Blocked", price = 1m })).StatusCode);
                Assert.False((await states.TransitionAsync(
                    MigrationService.Catalog, new TransitionRequest(MigrationStage.RollbackPending, 0),
                    flags, "test", "stale", CancellationToken.None)).Succeeded);
            }

            await using (var restarted = CreateGateway(settings.ConnectionString))
            {
                using var client = restarted.CreateClient();
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/api/products", new { name = "Blocked", price = 1m })).StatusCode);
            }
            await using (var stale = CreateGateway(settings.ConnectionString, emergencyPause: true))
            {
                using var client = stale.CreateClient();
                Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
            }

            var unavailableSettings = new NpgsqlConnectionStringBuilder(settings.ConnectionString) { Port = 1, Timeout = 1 };
            await using (var unavailable = CreateGateway(unavailableSettings.ConnectionString))
            {
                using var client = unavailable.CreateClient();
                Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
            }
            await using (var recovered = CreateGateway(settings.ConnectionString))
            {
                using var client = recovered.CreateClient();
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/api/products", new { name = "Blocked", price = 1m })).StatusCode);
            }
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static WebApplicationFactory<GatewayProgram> CreateGateway(string connection, bool emergencyPause = false) =>
        new WebApplicationFactory<GatewayProgram>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:MigrationControl", connection)
            .UseSetting("MigrationControl:AdminToken", "isolated-test-token")
            .UseSetting("MigrationControl:EmergencyPause", emergencyPause.ToString()));
}