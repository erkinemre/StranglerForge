using System.Security.Cryptography;
using System.Text;

namespace StranglerForge.Gateway.MigrationControl;

public enum MigrationService { Catalog, Inventory, Orders }
public enum MigrationStage { LegacyActive, ShadowValidation, CanaryReady, ModernActive, RollbackPending, ReconciliationRequired, LegacyRetired }
public enum DataOwner { Legacy, Modern }

public sealed record ServiceMigrationFlags(DataOwner Owner, int ModernReadPercent, bool LegacyReadAvailable, bool ShadowReads, int ShadowSamplePercent, bool WritesSinceCheckpoint, string? Checkpoint);
public sealed record MigrationFlags(bool EmergencyPause, ServiceMigrationFlags Catalog, ServiceMigrationFlags Inventory, ServiceMigrationFlags Orders)
{
    public ServiceMigrationFlags For(MigrationService service) => service switch
    {
        MigrationService.Catalog => Catalog,
        MigrationService.Inventory => Inventory,
        _ => Orders
    };
}

public static class MigrationFlagReader
{
    private static readonly int[] AllowedPercentages = [0, 10, 25, 50, 100];
    public static MigrationFlags Read(IConfiguration configuration)
    {
        var section = configuration.GetSection("MigrationControl");
        ServiceMigrationFlags ReadService(string name)
        {
            var item = section.GetSection(name);
            if (!Enum.TryParse<DataOwner>(item["Owner"], true, out var owner))
                throw new InvalidOperationException($"MigrationControl:{name}:Owner must be Legacy or Modern.");
            var percent = item.GetValue<int>("ModernReadPercent");
            var sample = item.GetValue<int>("ShadowSamplePercent");
            var legacyRead = item.GetValue<bool>("LegacyReadAvailable");
            var shadow = item.GetValue<bool>("ShadowReads");
            var writes = item.GetValue<bool>("WritesSinceCheckpoint");
            var checkpoint = item["Checkpoint"];
            if (!AllowedPercentages.Contains(percent) || sample is < 0 or > 100)
                throw new InvalidOperationException($"MigrationControl:{name} has an invalid traffic percentage.");
            if (owner == DataOwner.Modern && (percent != 100 || legacyRead || shadow))
                throw new InvalidOperationException($"MigrationControl:{name} cannot route reads to stale legacy data after modern ownership.");
            if (owner == DataOwner.Legacy && !legacyRead && (percent != 100 || shadow))
                throw new InvalidOperationException($"MigrationControl:{name} requires an available legacy read endpoint.");
            if (shadow && sample == 0)
                throw new InvalidOperationException($"MigrationControl:{name} shadow sampling must be positive when enabled.");
            return new(owner, percent, legacyRead, shadow, sample, writes, checkpoint);
        }
        var normalized = section.GetSection("NormalizedFields").Get<string[]>() ?? [];
        var allowedNormalization = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "generatedAt", "generatedAtUtc", "lastModifiedUtc", "traceId", "requestId" };
        if (normalized.Any(field => !allowedNormalization.Contains(field)))
            throw new InvalidOperationException("MigrationControl:NormalizedFields contains a business or identifier field.");        var flags = new MigrationFlags(section.GetValue<bool>("EmergencyPause"), ReadService("Catalog"), ReadService("Inventory"), ReadService("Orders"));
        if (flags.Inventory.Owner != DataOwner.Modern || flags.Orders.Owner != DataOwner.Modern)
            throw new InvalidOperationException("This deployment's Inventory and Orders routes require modern ownership; legacy endpoints are not complete or current.");
        return flags;
    }
}

public static class TrafficAssignment
{
    public static bool IsModern(string key, int percentage)
    {
        if (percentage <= 0) return false;
        if (percentage >= 100) return true;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var bucket = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(hash) % 100;
        return bucket < percentage;
    }
}
