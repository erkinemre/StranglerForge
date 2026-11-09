using System.Text.Json;

namespace StranglerForge.Gateway.MigrationControl;

public sealed record ServiceMigrationState(MigrationStage Stage, long Version, DateTime UpdatedAtUtc);
public sealed record MigrationAudit(DateTime AtUtc, MigrationService Service, MigrationStage From, MigrationStage To, long Version, string Actor, string CorrelationId);
public sealed record MigrationSnapshot(Dictionary<MigrationService, ServiceMigrationState> Services, List<MigrationAudit> Audit);
public sealed record TransitionRequest(MigrationStage Target, long ExpectedVersion);
public sealed record TransitionResult(bool Succeeded, string? Error, ServiceMigrationState? State);
public sealed record RollbackEligibility(bool Eligible, IReadOnlyList<string> Reasons);

public sealed class MigrationStateStore
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string path;
    private MigrationSnapshot snapshot;

    public MigrationStateStore(IHostEnvironment environment, IConfiguration configuration)
    {
        var root = Path.GetFullPath(environment.ContentRootPath);
        path = Path.GetFullPath(Path.Combine(root, configuration["MigrationControl:StateFile"] ?? "migration-state.json"));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Migration state file must remain inside the gateway content root.");
        snapshot = File.Exists(path)
            ? JsonSerializer.Deserialize<MigrationSnapshot>(File.ReadAllText(path)) ?? throw new InvalidOperationException("Migration state file is invalid.")
            : new(new Dictionary<MigrationService, ServiceMigrationState>
            {
                [MigrationService.Catalog] = new(MigrationFlagReader.Read(configuration).Catalog.Owner == DataOwner.Legacy ? MigrationStage.LegacyActive : MigrationStage.ModernActive, 0, DateTime.UtcNow),
                [MigrationService.Inventory] = new(MigrationStage.ModernActive, 0, DateTime.UtcNow),
                [MigrationService.Orders] = new(MigrationStage.ModernActive, 0, DateTime.UtcNow)
            }, []);
        if (snapshot.Services.Count != 3 || Enum.GetValues<MigrationService>().Any(service => !snapshot.Services.ContainsKey(service)))
            throw new InvalidOperationException("Migration state file is incomplete.");
    }

    public ServiceMigrationState Get(MigrationService service) => snapshot.Services[service];
    public IReadOnlyList<MigrationAudit> Audit => snapshot.Audit.AsReadOnly();

    public RollbackEligibility Evaluate(MigrationService service, MigrationFlags flags)
    {
        var state = Get(service);
        var item = flags.For(service);
        var reasons = new List<string>();
        if (item.Owner == DataOwner.Modern) reasons.Add("Modern PostgreSQL is authoritative; SQL reconciliation and ownership transfer are unverified.");
        if (item.WritesSinceCheckpoint) reasons.Add("Post-cutover writes have occurred since the last migration checkpoint.");
        if (string.IsNullOrWhiteSpace(item.Checkpoint)) reasons.Add("No verified migration checkpoint is recorded.");
        if (service == MigrationService.Orders && item.Owner == DataOwner.Modern)
            reasons.Add("Active sagas, uncertain payments, Inventory reservations, and pending outbox delivery have not been proven clear.");
        if (service == MigrationService.Inventory && item.Owner == DataOwner.Modern)
            reasons.Add("Pending reservations, adjustments, and outbox delivery have not been reconciled to SQL Server.");
        if (state.Stage is MigrationStage.ModernActive or MigrationStage.RollbackPending or MigrationStage.ReconciliationRequired)
            reasons.Add("No independently verified reconciliation evidence permits restoring legacy ownership.");
        if (state.Stage == MigrationStage.LegacyRetired) reasons.Add("The legacy implementation is retired.");
        return new(reasons.Count == 0, reasons);
    }

    public async Task<TransitionResult> TransitionAsync(MigrationService service, TransitionRequest request, MigrationFlags flags, string actor, string correlationId, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var current = snapshot.Services[service];
            if (request.ExpectedVersion != current.Version) return new(false, "Migration state changed; reload and retry with the current version.", current);
            var allowed = (current.Stage, request.Target) switch
            {
                (MigrationStage.LegacyActive, MigrationStage.ShadowValidation) => true,
                (MigrationStage.ShadowValidation, MigrationStage.CanaryReady) => true,
                (MigrationStage.CanaryReady, MigrationStage.ModernActive) => true,
                (MigrationStage.ModernActive, MigrationStage.RollbackPending) => true,
                (MigrationStage.RollbackPending, MigrationStage.ReconciliationRequired) => true,
                (MigrationStage.ReconciliationRequired, MigrationStage.LegacyActive) => true,
                (MigrationStage.ModernActive, MigrationStage.LegacyRetired) => true,
                _ => false
            };
            if (!allowed) return new(false, "The requested migration transition is not permitted.", current);
            if ((request.Target is MigrationStage.LegacyActive or MigrationStage.ShadowValidation or MigrationStage.CanaryReady) && flags.For(service).Owner != DataOwner.Legacy)
                return new(false, "Legacy reads require verified legacy data ownership.", current);
            if (request.Target == MigrationStage.ModernActive && flags.For(service).Owner != DataOwner.Modern)
                return new(false, "ModernActive requires modern data ownership and an external cutover checkpoint.", current);
            if (request.Target == MigrationStage.LegacyActive && !Evaluate(service, flags).Eligible)
                return new(false, string.Join(' ', Evaluate(service, flags).Reasons), current);
            var next = current with { Stage = request.Target, Version = current.Version + 1, UpdatedAtUtc = DateTime.UtcNow };
            var services = new Dictionary<MigrationService, ServiceMigrationState>(snapshot.Services) { [service] = next };
            var audit = new List<MigrationAudit>(snapshot.Audit) { new(DateTime.UtcNow, service, current.Stage, next.Stage, next.Version, actor, correlationId) };
            if (audit.Count > 1000) audit.RemoveRange(0, audit.Count - 1000);
            var updated = new MigrationSnapshot(services, audit);
            var temporary = path + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(updated), ct);
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            snapshot = updated;
            return new(true, null, next);
        }
        finally { gate.Release(); }
    }
}
