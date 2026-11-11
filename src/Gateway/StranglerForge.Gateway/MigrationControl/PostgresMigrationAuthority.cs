using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace StranglerForge.Gateway.MigrationControl;

// A PostgreSQL row lock spans the routed request, so cutover cannot pass an in-flight write.
public sealed class PostgresMigrationAuthority(string connectionString)
{
    private readonly SemaphoreSlim initialization = new(1, 1);
    private bool initialized;

    public static string Fingerprint(MigrationFlags flags) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(flags))));

    private async Task EnsureInitializedAsync(MigrationSnapshot initial, MigrationFlags flags, CancellationToken ct)
    {
        if (initialized) return;
        await initialization.WaitAsync(ct);
        try
        {
            if (initialized) return;
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            // The transaction-scoped lock serializes first-time schema creation across replicas and is released on failure.
            await using (var bootstrap = new NpgsqlCommand("SELECT pg_advisory_xact_lock(77007, 8)", connection, transaction))
                await bootstrap.ExecuteNonQueryAsync(ct);
            await using (var schema = new NpgsqlCommand("CREATE TABLE IF NOT EXISTS migration_authority (id integer PRIMARY KEY CHECK (id = 1), revision bigint NOT NULL, snapshot text NOT NULL, config_hash text NOT NULL, config_json text NOT NULL)", connection, transaction))
                await schema.ExecuteNonQueryAsync(ct);
            await using (var audit = new NpgsqlCommand("CREATE TABLE IF NOT EXISTS migration_configuration_audit (revision bigint PRIMARY KEY, occurred_at_utc timestamptz NOT NULL, actor text NOT NULL, correlation_id text NOT NULL, old_hash text NOT NULL, new_hash text NOT NULL)", connection, transaction))
                await audit.ExecuteNonQueryAsync(ct);
            await using var insert = new NpgsqlCommand("INSERT INTO migration_authority (id, revision, snapshot, config_hash, config_json) VALUES (1, 0, @snapshot, @hash, @config) ON CONFLICT (id) DO NOTHING", connection, transaction);
            insert.Parameters.AddWithValue("snapshot", JsonSerializer.Serialize(initial));
            insert.Parameters.AddWithValue("hash", Fingerprint(flags));
            insert.Parameters.AddWithValue("config", JsonSerializer.Serialize(flags));
            await insert.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
            initialized = true;
        }
        finally { initialization.Release(); }
    }

    public async Task<MigrationAuthorityLease> AcquireAsync(MigrationSnapshot initial, MigrationFlags flags, bool exclusive, CancellationToken ct)
    {
        await EnsureInitializedAsync(initial, flags, ct);
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct);
            var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            try
            {
                await using var query = new NpgsqlCommand($"SELECT revision, snapshot, config_hash FROM migration_authority WHERE id = 1 FOR {(exclusive ? "UPDATE" : "SHARE")}", connection, transaction);
                await using var reader = await query.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Migration authority is missing its state row.");
                var revision = reader.GetInt64(0);
                var snapshot = JsonSerializer.Deserialize<MigrationSnapshot>(reader.GetString(1)) ?? throw new InvalidOperationException("Migration authority snapshot is invalid.");
                var hash = reader.GetString(2);
                await reader.DisposeAsync();
                if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(Fingerprint(flags))))
                    throw new InvalidOperationException("Gateway migration flags differ from the active shared configuration revision.");
                return new MigrationAuthorityLease(connection, transaction, snapshot, revision);
            }
            catch { await transaction.DisposeAsync(); throw; }
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task<long?> ActivateConfigurationAsync(MigrationSnapshot initial, MigrationFlags flags, long expectedRevision, string actor, string correlationId, CancellationToken ct)
    {
        await EnsureInitializedAsync(initial, flags, ct);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var query = new NpgsqlCommand("SELECT revision, config_hash FROM migration_authority WHERE id = 1 FOR UPDATE", connection, transaction);
        await using var reader = await query.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Migration authority is missing its state row.");
        var revision = reader.GetInt64(0);
        var priorHash = reader.GetString(1);
        await reader.DisposeAsync();
        if (revision != expectedRevision) return null;
        var nextHash = Fingerprint(flags);
        if (priorHash == nextHash) return revision;
        await using var update = new NpgsqlCommand("UPDATE migration_authority SET revision = revision + 1, config_hash = @hash, config_json = @config WHERE id = 1", connection, transaction);
        update.Parameters.AddWithValue("hash", nextHash);
        update.Parameters.AddWithValue("config", JsonSerializer.Serialize(flags));
        await update.ExecuteNonQueryAsync(ct);
        await using var audit = new NpgsqlCommand("INSERT INTO migration_configuration_audit (revision, occurred_at_utc, actor, correlation_id, old_hash, new_hash) VALUES (@revision, now(), @actor, @correlation, @old, @new)", connection, transaction);
        audit.Parameters.AddWithValue("revision", revision + 1);
        audit.Parameters.AddWithValue("actor", actor);
        audit.Parameters.AddWithValue("correlation", correlationId);
        audit.Parameters.AddWithValue("old", priorHash);
        audit.Parameters.AddWithValue("new", nextHash);
        await audit.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return revision + 1;
    }
}

public sealed class MigrationAuthorityLease(NpgsqlConnection connection, NpgsqlTransaction transaction, MigrationSnapshot snapshot, long revision) : IAsyncDisposable
{
    public MigrationSnapshot Snapshot { get; } = snapshot;
    public long Revision { get; } = revision;
    public async Task CommitAsync(MigrationSnapshot updated, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("UPDATE migration_authority SET revision = revision + 1, snapshot = @snapshot WHERE id = 1 AND revision = @revision", connection, transaction);
        command.Parameters.AddWithValue("snapshot", JsonSerializer.Serialize(updated));
        command.Parameters.AddWithValue("revision", Revision);
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new DBConcurrencyException("Migration authority revision changed.");
        await transaction.CommitAsync(ct);
    }
    public async ValueTask DisposeAsync()
    {
        await transaction.DisposeAsync();
        await connection.DisposeAsync();
    }
}
