using Xunit;
using Microsoft.Extensions.Configuration;
using Npgsql;
using StranglerForge.Gateway.MigrationControl;

namespace MigrationAuthority.LiveTests;

public sealed class CoordinationTests
{
    [Fact]
    public async Task TwoInstancesSerializeTransitionsAndRejectStaleConfiguration()
    {
        var baseConnection = Environment.GetEnvironmentVariable("MIGRATION_TEST_CONNECTION")
            ?? throw new InvalidOperationException("MIGRATION_TEST_CONNECTION must point to an isolated PostgreSQL server. Run this live test explicitly.");
        var schemaName = "migration_authority_test_" + Guid.NewGuid().ToString("N");
        var testBuilder = new NpgsqlConnectionStringBuilder(baseConnection) { SearchPath = schemaName, Pooling = false };
        await using var admin = new NpgsqlConnection(baseConnection);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schemaName}", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            var flags = Flags();
            var initial = new MigrationSnapshot(new Dictionary<MigrationService, ServiceMigrationState>
            {
                [MigrationService.Catalog] = new(MigrationStage.ModernActive, 0, DateTime.UtcNow),
                [MigrationService.Inventory] = new(MigrationStage.ModernActive, 0, DateTime.UtcNow),
                [MigrationService.Orders] = new(MigrationStage.ModernActive, 0, DateTime.UtcNow)
            }, []);
            var first = new PostgresMigrationAuthority(testBuilder.ConnectionString);
            var second = new PostgresMigrationAuthority(testBuilder.ConnectionString);
            var initialReads = await Task.WhenAll(
                first.AcquireAsync(initial, flags, false, CancellationToken.None),
                second.AcquireAsync(initial, flags, false, CancellationToken.None));
            foreach (var initialRead in initialReads) await initialRead.DisposeAsync();
            await using (var lease = await first.AcquireAsync(initial, flags, true, CancellationToken.None))
            {
                var changed = new MigrationSnapshot(new(lease.Snapshot.Services)
                {
                    [MigrationService.Catalog] = new(MigrationStage.RollbackPending, 1, DateTime.UtcNow)
                }, [new(DateTime.UtcNow, MigrationService.Catalog, MigrationStage.ModernActive, MigrationStage.RollbackPending, 1, "test", "test-correlation")]);
                await lease.CommitAsync(changed, CancellationToken.None);
            }
            await using (var lease = await second.AcquireAsync(initial, flags, false, CancellationToken.None))
            {
                Assert.Equal(1, lease.Revision);
                Assert.Equal(MigrationStage.RollbackPending, lease.Snapshot.Services[MigrationService.Catalog].Stage);
                Assert.Single(lease.Snapshot.Audit);
            }
            var firstLease = await first.AcquireAsync(initial, flags, true, CancellationToken.None);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var competing = Task.Run(async () =>
            {
                started.SetResult();
                return await second.AcquireAsync(initial, flags, true, CancellationToken.None);
            });
            await started.Task;
            Assert.NotSame(competing, await Task.WhenAny(competing, Task.Delay(150)));
            await firstLease.DisposeAsync();
            await using (var acquired = await competing) Assert.Equal(1, acquired.Revision);
            var changedFlags = flags with { EmergencyPause = true };
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await using var _ = await second.AcquireAsync(initial, changedFlags, false, CancellationToken.None);
            });
            Assert.Null(await first.ActivateConfigurationAsync(initial, changedFlags, 0, "test", "stale", CancellationToken.None));
            Assert.Equal(2, await first.ActivateConfigurationAsync(initial, changedFlags, 1, "test", "test-correlation", CancellationToken.None));
            await using var restarted = await new PostgresMigrationAuthority(testBuilder.ConnectionString).AcquireAsync(initial, changedFlags, false, CancellationToken.None);
            Assert.Equal(2, restarted.Revision);
            Assert.Equal(MigrationStage.RollbackPending, restarted.Snapshot.Services[MigrationService.Catalog].Stage);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schemaName} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static MigrationFlags Flags()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MigrationControl:Catalog:Owner"] = "Modern",
            ["MigrationControl:Catalog:ModernReadPercent"] = "100",
            ["MigrationControl:Inventory:Owner"] = "Modern",
            ["MigrationControl:Inventory:ModernReadPercent"] = "100",
            ["MigrationControl:Orders:Owner"] = "Modern",
            ["MigrationControl:Orders:ModernReadPercent"] = "100"
        }).Build();
        return MigrationFlagReader.Read(config);
    }
}
