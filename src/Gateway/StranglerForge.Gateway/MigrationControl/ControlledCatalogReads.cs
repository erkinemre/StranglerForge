using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;

namespace StranglerForge.Gateway.MigrationControl;

public sealed record ComparisonRecord(Guid CorrelationId, string Route, string Primary, string Shadow, DateTime AtUtc, string Outcome, string? Category);
public sealed class MigrationDiagnostics
{
    private readonly ConcurrentQueue<ComparisonRecord> comparisons = new();
    private readonly ConcurrentDictionary<string, long> counts = new();
    private readonly Meter meter = new("StranglerForge.Migration", "1.0.0");
    private readonly Counter<long> events;
    public MigrationDiagnostics() => events = meter.CreateCounter<long>("migration.events");
    public void Count(string kind)
    {
        counts.AddOrUpdate(kind, 1, (_, previous) => previous + 1);
        events.Add(1, new KeyValuePair<string, object?>("kind", kind));
    }
    public void Record(ComparisonRecord record)
    {
        comparisons.Enqueue(record);
        while (comparisons.Count > 200) comparisons.TryDequeue(out _);
        Count(record.Outcome == "Match" ? "shadow.match" : "shadow.mismatch");
    }
    public object Snapshot() => new { counts = counts.ToDictionary(x => x.Key, x => x.Value), recentComparisons = comparisons.ToArray() };
}

public static class ContractComparison
{
    public static string? Compare(HttpStatusCode primaryStatus, string primaryBody, HttpStatusCode shadowStatus, string shadowBody, IReadOnlySet<string> ignoredFields)
    {
        if (primaryStatus != shadowStatus) return "StatusCode";
        try
        {
            using var primary = JsonDocument.Parse(primaryBody);
            using var shadow = JsonDocument.Parse(shadowBody);
            return CompareValues(primary.RootElement, shadow.RootElement, ignoredFields);
        }
        catch (JsonException) { return "InvalidJson"; }
    }

    public static string? ValidateCatalogList(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return "Structure";
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("id", out var id) || !Guid.TryParse(id.ToString(), out _) ||
                    !item.TryGetProperty("name", out var name) || string.IsNullOrWhiteSpace(name.ToString()) ||
                    !item.TryGetProperty("price", out var price) || price.ValueKind != JsonValueKind.Number)
                    return "RequiredField";
            }
            return null;
        }
        catch (JsonException) { return "InvalidJson"; }
    }
    private static string? CompareValues(JsonElement primary, JsonElement shadow, IReadOnlySet<string> ignored)
    {
        if (primary.ValueKind != shadow.ValueKind) return "Structure";
        if (primary.ValueKind == JsonValueKind.Object)
        {
            var left = primary.EnumerateObject().Where(x => !ignored.Contains(x.Name)).ToDictionary(x => x.Name, x => x.Value, StringComparer.OrdinalIgnoreCase);
            var right = shadow.EnumerateObject().Where(x => !ignored.Contains(x.Name)).ToDictionary(x => x.Name, x => x.Value, StringComparer.OrdinalIgnoreCase);
            if (!left.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(right.Keys)) return "MissingField";
            foreach (var (name, value) in left)
            {
                var mismatch = CompareValues(value, right[name], ignored);
                if (mismatch is not null) return name.Equals("id", StringComparison.OrdinalIgnoreCase) ? "Identifier" : mismatch;
            }
            return null;
        }
        if (primary.ValueKind == JsonValueKind.Array)
        {
            if (primary.GetArrayLength() != shadow.GetArrayLength()) return "CollectionLength";
            for (var i = 0; i < primary.GetArrayLength(); i++)
            {
                var mismatch = CompareValues(primary[i], shadow[i], ignored);
                if (mismatch is not null) return mismatch;
            }
            return null;
        }
        return primary.ToString() == shadow.ToString() ? null : "BusinessValue";
    }
}

public sealed class ControlledCatalogReads(IConfiguration configuration, IHttpClientFactory factory, MigrationStateStore states, MigrationDiagnostics diagnostics, ILogger<ControlledCatalogReads> logger)
{
    private readonly SemaphoreSlim shadowLimit = new(8, 8);
    public async Task<IResult> HandleAsync(HttpContext context)
    {
        MigrationFlags flags;
        try { flags = MigrationFlagReader.Read(configuration); }
        catch (Exception error) when (error is InvalidOperationException or FormatException)
        {
            diagnostics.Count("configuration.invalid");
            logger.LogError(error, "Migration configuration rejected");
            return Results.StatusCode(503);
        }
        if (flags.EmergencyPause) { diagnostics.Count("migration.paused"); return Results.StatusCode(503); }
        var catalog = flags.Catalog;
        var stage = states.Get(MigrationService.Catalog).Stage;
        if (catalog.Owner == DataOwner.Legacy && stage is not (MigrationStage.LegacyActive or MigrationStage.ShadowValidation or MigrationStage.CanaryReady))
            return Results.StatusCode(503);
        if (catalog.Owner == DataOwner.Modern && stage is not (MigrationStage.ModernActive or MigrationStage.RollbackPending or MigrationStage.ReconciliationRequired or MigrationStage.LegacyRetired))
            return Results.StatusCode(503);
        var key = context.Request.Headers["X-Migration-Key"].ToString();
        var modern = catalog.Owner == DataOwner.Modern || (stage == MigrationStage.CanaryReady && (string.IsNullOrWhiteSpace(key) || TrafficAssignment.IsModern(key, catalog.ModernReadPercent)));
        var primary = modern ? "catalog" : "legacy";
        var secondary = modern ? "legacy" : "catalog";
        var correlation = Guid.Parse(context.Request.Headers["X-Correlation-ID"].ToString());
        using var client = factory.CreateClient("migration-read");
        var primaryResponse = await GetAsync(client, primary, context, TimeSpan.FromSeconds(3), context.RequestAborted);
        if (primaryResponse is null) { diagnostics.Count("gateway.read_error"); return Results.Problem("Catalog destination unavailable.", statusCode: 503); }
        diagnostics.Count(modern ? "route.modern" : "route.legacy");
        if (stage == MigrationStage.CanaryReady) diagnostics.Count(modern ? "canary.modern" : "canary.legacy");
        if (catalog.ShadowReads && stage is MigrationStage.ShadowValidation or MigrationStage.CanaryReady &&
            TrafficAssignment.IsModern(key.Length == 0 ? correlation.ToString("D") : key, catalog.ShadowSamplePercent) && shadowLimit.Wait(0))
        {
            diagnostics.Count("shadow.request");
            try
            {
                var shadowResponse = await GetAsync(client, secondary, context, TimeSpan.FromMilliseconds(300), context.RequestAborted);
                var category = shadowResponse is null ? "ShadowUnavailable" : ContractComparison.Compare(primaryResponse.Value.Status, primaryResponse.Value.Body, shadowResponse.Value.Status, shadowResponse.Value.Body,
                    configuration.GetSection("MigrationControl:NormalizedFields").Get<string[]>()?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                if (category is null && primaryResponse.Value.Status == HttpStatusCode.OK && shadowResponse is not null)
                    category = ContractComparison.ValidateCatalogList(primaryResponse.Value.Body) ?? ContractComparison.ValidateCatalogList(shadowResponse.Value.Body);
                diagnostics.Record(new(correlation, "catalog", primary, secondary, DateTime.UtcNow, category is null ? "Match" : "Mismatch", category));
            }
            finally { shadowLimit.Release(); }
        }
        foreach (var (name, value) in primaryResponse.Value.Headers) context.Response.Headers[name] = value;
        return Results.Content(primaryResponse.Value.Body, primaryResponse.Value.ContentType, statusCode: (int)primaryResponse.Value.Status);
    }

    private async Task<(HttpStatusCode Status, string Body, string ContentType, Dictionary<string, string[]> Headers)?> GetAsync(HttpClient client, string destination, HttpContext context, TimeSpan timeout, CancellationToken abort)
    {
        var baseUrl = configuration[$"ReverseProxy:Clusters:{destination}:Destinations:{(destination == "legacy" ? "monolith" : "service")}:Address"];
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, "api/products" + context.Request.QueryString));
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", context.Request.Headers["X-Correlation-ID"].ToString());
        request.Headers.Accept.ParseAdd("application/json");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(abort);
        timeoutSource.CancelAfter(timeout);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);
            if (response.Content.Headers.ContentLength is > 262144) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(timeoutSource.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeoutSource.Token)) > 0)
            {
                if (buffer.Length + read > 262144) return null;
                buffer.Write(chunk, 0, read);
            }
            var headers = response.Headers.Concat(response.Content.Headers)
                .Where(x => x.Key.Equals("ETag", StringComparison.OrdinalIgnoreCase) || x.Key.Equals("Cache-Control", StringComparison.OrdinalIgnoreCase) || x.Key.Equals("Last-Modified", StringComparison.OrdinalIgnoreCase) || x.Key.Equals("X-Legacy", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
            return (response.StatusCode, System.Text.Encoding.UTF8.GetString(buffer.ToArray()), response.Content.Headers.ContentType?.ToString() ?? "application/json", headers);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
        {
            logger.LogWarning("{Destination} Catalog read failed: {ErrorType}", destination, error.GetType().Name);
            return null;
        }
    }
}
