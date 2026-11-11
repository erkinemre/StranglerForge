using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using StranglerForge.Gateway.MigrationControl;

namespace StranglerForge.GatewayTests;

public sealed class MigrationControlTests
{
    private static IConfigurationRoot Configuration(DataOwner owner = DataOwner.Modern) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["MigrationControl:Catalog:Owner"] = owner.ToString(),
        ["MigrationControl:Catalog:ModernReadPercent"] = "100",
        ["MigrationControl:Catalog:LegacyReadAvailable"] = "false",
        ["MigrationControl:Catalog:ShadowReads"] = "false",
        ["MigrationControl:Catalog:ShadowSamplePercent"] = "0",
        ["MigrationControl:Catalog:WritesSinceCheckpoint"] = "true",
        ["MigrationControl:Inventory:Owner"] = "Modern",
        ["MigrationControl:Inventory:ModernReadPercent"] = "100",
        ["MigrationControl:Inventory:WritesSinceCheckpoint"] = "true",
        ["MigrationControl:Orders:Owner"] = "Modern",
        ["MigrationControl:Orders:ModernReadPercent"] = "100",
        ["MigrationControl:Orders:WritesSinceCheckpoint"] = "true"
    }).Build();

    [Fact]
    public void DefaultsAreModernAndInvalidStaleLegacyRoutingFailsClosed()
    {
        var config = Configuration();
        Assert.Equal(DataOwner.Modern, MigrationFlagReader.Read(config).Catalog.Owner);
        config["MigrationControl:Catalog:ModernReadPercent"] = "10";
        Assert.Throws<InvalidOperationException>(() => MigrationFlagReader.Read(config));
        config["MigrationControl:Catalog:ModernReadPercent"] = "100";
        config["MigrationControl:Catalog:ShadowReads"] = "true";
        Assert.Throws<InvalidOperationException>(() => MigrationFlagReader.Read(config));
    }

    [Theory]
    [InlineData(0)] [InlineData(10)] [InlineData(25)] [InlineData(50)] [InlineData(100)]
    public void CanaryAssignmentIsStableAndMonotonic(int percentage)
    {
        var first = TrafficAssignment.IsModern("stable-client", percentage);
        Assert.Equal(first, TrafficAssignment.IsModern("stable-client", percentage));
        if (percentage == 0) Assert.False(first);
        if (percentage == 100) Assert.True(first);
        Assert.True(!first || TrafficAssignment.IsModern("stable-client", 100));
    }

    [Fact]
    public void ContractComparisonIdentifiesRealDifferencesAndExplicitNormalization()
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "generatedAt" };
        Assert.Null(ContractComparison.Compare(HttpStatusCode.OK, "{\"id\":\"a\",\"price\":12,\"generatedAt\":1}", HttpStatusCode.OK, "{\"price\":12,\"id\":\"a\",\"generatedAt\":2}", ignored));
        Assert.Equal("StatusCode", ContractComparison.Compare(HttpStatusCode.OK, "{}", HttpStatusCode.NotFound, "{}", ignored));
        Assert.Equal("MissingField", ContractComparison.Compare(HttpStatusCode.OK, "{\"id\":\"a\"}", HttpStatusCode.OK, "{}", ignored));
        Assert.Equal("Identifier", ContractComparison.Compare(HttpStatusCode.OK, "{\"id\":\"a\"}", HttpStatusCode.OK, "{\"id\":\"b\"}", ignored));
        Assert.Equal("BusinessValue", ContractComparison.Compare(HttpStatusCode.OK, "{\"price\":12}", HttpStatusCode.OK, "{\"price\":13}", ignored));
        Assert.Equal("CollectionLength", ContractComparison.Compare(HttpStatusCode.OK, "[1]", HttpStatusCode.OK, "[1,2]", ignored));
    }

    [Fact]
    public async Task RollbackIsBlockedAfterWritesAndTransitionsAreVersioned()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "migration-state-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = Configuration();
            var store = new MigrationStateStore(new TestEnvironment(root), config);
            var flags = MigrationFlagReader.Read(config);
            var eligibility = store.Evaluate(MigrationService.Orders, flags);
            Assert.False(eligibility.Eligible);
            Assert.Contains(eligibility.Reasons, x => x.Contains("sagas", StringComparison.OrdinalIgnoreCase));
            var first = await store.TransitionAsync(MigrationService.Orders, new(MigrationStage.RollbackPending, 0), flags, "test", Guid.NewGuid().ToString("D"), CancellationToken.None);
            Assert.True(first.Succeeded);
            Assert.Equal(1, first.State!.Version);
            var stale = await store.TransitionAsync(MigrationService.Orders, new(MigrationStage.ReconciliationRequired, 0), flags, "test", "", CancellationToken.None);
            Assert.False(stale.Succeeded);
            var second = await store.TransitionAsync(MigrationService.Orders, new(MigrationStage.ReconciliationRequired, 1), flags, "test", "", CancellationToken.None);
            Assert.True(second.Succeeded);
            var forbidden = await store.TransitionAsync(MigrationService.Orders, new(MigrationStage.LegacyActive, 2), flags, "test", "", CancellationToken.None);
            Assert.False(forbidden.Succeeded);
            Assert.Equal(2, store.Audit.Count);
            Assert.Equal(MigrationStage.ReconciliationRequired, new MigrationStateStore(new TestEnvironment(root), config).Get(MigrationService.Orders).Stage);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ShadowMismatchDoesNotChangePrimaryCatalogResponse()
    {
        await using var legacy = await StartCatalogAsync("legacy", 12);
        await using var modern = await StartCatalogAsync("modern", 13);
        var stateFile = "migration-state-test-" + Guid.NewGuid().ToString("N") + ".json";
        var gateway = new WebApplicationFactory<GatewayProgram>().WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MigrationControl:Catalog:Owner"] = "Legacy",
            ["MigrationControl:Catalog:ModernReadPercent"] = "0",
            ["MigrationControl:Catalog:LegacyReadAvailable"] = "true",
            ["MigrationControl:Catalog:ShadowReads"] = "true",
            ["MigrationControl:Catalog:ShadowSamplePercent"] = "100",
            ["MigrationControl:StateFile"] = stateFile,
            ["ReverseProxy:Clusters:legacy:Destinations:monolith:Address"] = legacy.Urls.Single(),
            ["ReverseProxy:Clusters:catalog:Destinations:service:Address"] = modern.Urls.Single(),
            ["ReverseProxy:Routes:catalog:ClusterId"] = "legacy"
        })));
        var statePath = Path.Combine(gateway.Server.Services.GetRequiredService<IHostEnvironment>().ContentRootPath, stateFile);
        try
        {
            using var client = gateway.CreateClient();
            var store = gateway.Services.GetRequiredService<MigrationStateStore>();
            var flags = MigrationFlagReader.Read(gateway.Services.GetRequiredService<IConfiguration>());
            Assert.True((await store.TransitionAsync(MigrationService.Catalog, new(MigrationStage.ShadowValidation, 0), flags, "test", "", CancellationToken.None)).Succeeded);
            using var response = await client.GetAsync("/api/products?limit=2");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("legacy", response.Headers.GetValues("X-Legacy").Single());
            var products = await response.Content.ReadFromJsonAsync<ProductStub[]>();
            Assert.Equal(12, products![0].Price);
            var snapshot = System.Text.Json.JsonSerializer.Serialize(gateway.Services.GetRequiredService<MigrationDiagnostics>().Snapshot());
            Assert.Contains("BusinessValue", snapshot);
        }
        finally
        {
            await gateway.DisposeAsync();
            if (File.Exists(statePath)) File.Delete(statePath);
        }
    }

    private static async Task<WebApplication> StartCatalogAsync(string label, decimal price)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapGet("/api/products", (HttpContext context) => { context.Response.Headers["X-Legacy"] = label; return Results.Ok(new[] { new { id = "00000000-0000-0000-0000-000000000201", name = "Widget", price } }); });
        await app.StartAsync();
        return app;
    }
    private sealed record ProductStub(string Id, decimal Price);
    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "GatewayTests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(root);
    }
    [Fact]
    public void RuntimeFlagChangesAreValidatedOnEveryRead()
    {
        var config = Configuration(DataOwner.Legacy);
        config["MigrationControl:Catalog:LegacyReadAvailable"] = "true";
        config["MigrationControl:Catalog:ModernReadPercent"] = "25";
        Assert.Equal(25, MigrationFlagReader.Read(config).Catalog.ModernReadPercent);
        config["MigrationControl:Catalog:ModernReadPercent"] = "50";
        Assert.Equal(50, MigrationFlagReader.Read(config).Catalog.ModernReadPercent);
        config["MigrationControl:Catalog:ModernReadPercent"] = "30";
        Assert.Throws<InvalidOperationException>(() => MigrationFlagReader.Read(config));
    }

    [Fact]
    public void AdministrativeAuthorizationRequiresLoopbackAndConfiguredToken()
    {
        var context = new DefaultHttpContext();
        var config = Configuration();
        Assert.Equal(503, MigrationAdminAuthorization.Check(context, config));
        config["MigrationControl:AdminToken"] = "isolated-test-secret";
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
        context.Request.Headers["X-Migration-Admin-Token"] = "isolated-test-secret";
        Assert.Equal(403, MigrationAdminAuthorization.Check(context, config));
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Headers["X-Migration-Admin-Token"] = "wrong";
        Assert.Equal(401, MigrationAdminAuthorization.Check(context, config));
        context.Request.Headers["X-Migration-Admin-Token"] = "isolated-test-secret";
        Assert.Equal(200, MigrationAdminAuthorization.Check(context, config));
    }

    [Fact]
    public async Task ShadowTimeoutDoesNotChangePrimaryResponse()
    {
        await using var legacy = await StartCatalogAsync("legacy", 12);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var slow = builder.Build();
        slow.MapGet("/api/products", async () => { await Task.Delay(2000); return Results.Ok(new[] { new { id = "00000000-0000-0000-0000-000000000201", name = "Widget", price = 12 } }); });
        await slow.StartAsync();
        var stateFile = "migration-state-test-" + Guid.NewGuid().ToString("N") + ".json";
        await using var gateway = new WebApplicationFactory<GatewayProgram>().WithWebHostBuilder(host => host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MigrationControl:Catalog:Owner"] = "Legacy",
            ["MigrationControl:Catalog:ModernReadPercent"] = "0",
            ["MigrationControl:Catalog:LegacyReadAvailable"] = "true",
            ["MigrationControl:Catalog:ShadowReads"] = "true",
            ["MigrationControl:Catalog:ShadowSamplePercent"] = "100",
            ["MigrationControl:StateFile"] = stateFile,
            ["ReverseProxy:Clusters:legacy:Destinations:monolith:Address"] = legacy.Urls.Single(),
            ["ReverseProxy:Clusters:catalog:Destinations:service:Address"] = slow.Urls.Single(),
            ["ReverseProxy:Routes:catalog:ClusterId"] = "legacy"
        })));
        var path = Path.Combine(gateway.Server.Services.GetRequiredService<IHostEnvironment>().ContentRootPath, stateFile);
        try
        {
            var state = gateway.Services.GetRequiredService<MigrationStateStore>();
            var flags = MigrationFlagReader.Read(gateway.Services.GetRequiredService<IConfiguration>());
            Assert.True((await state.TransitionAsync(MigrationService.Catalog, new(MigrationStage.ShadowValidation, 0), flags, "test", "", CancellationToken.None)).Succeeded);
            using var response = await gateway.CreateClient().GetAsync("/api/products");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("legacy", response.Headers.GetValues("X-Legacy").Single());
            var snapshot = System.Text.Json.JsonSerializer.Serialize(gateway.Services.GetRequiredService<MigrationDiagnostics>().Snapshot());
            Assert.Contains("ShadowUnavailable", snapshot);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Theory]
    [InlineData(0)] [InlineData(25)] [InlineData(100)]
    public async Task CanaryReadsUseStableKeysWithoutMovingWriteOwnership(int percentage)
    {
        await using var legacy = await StartCatalogAsync("legacy", 12);
        await using var modern = await StartCatalogAsync("modern", 12);
        var stateFile = "migration-state-test-" + Guid.NewGuid().ToString("N") + ".json";
        await using var gateway = new WebApplicationFactory<GatewayProgram>().WithWebHostBuilder(host => host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MigrationControl:Catalog:Owner"] = "Legacy",
            ["MigrationControl:Catalog:ModernReadPercent"] = percentage.ToString(),
            ["MigrationControl:Catalog:Checkpoint"] = "fixture-verified",
            ["MigrationControl:Catalog:WritesSinceCheckpoint"] = "false",
            ["MigrationControl:Catalog:LegacyReadAvailable"] = "true",
            ["MigrationControl:StateFile"] = stateFile,
            ["ReverseProxy:Clusters:legacy:Destinations:monolith:Address"] = legacy.Urls.Single(),
            ["ReverseProxy:Clusters:catalog:Destinations:service:Address"] = modern.Urls.Single(),
            ["ReverseProxy:Routes:catalog:ClusterId"] = "legacy"
        })));
        var path = Path.Combine(gateway.Server.Services.GetRequiredService<IHostEnvironment>().ContentRootPath, stateFile);
        try
        {
            var state = gateway.Services.GetRequiredService<MigrationStateStore>();
            var flags = MigrationFlagReader.Read(gateway.Services.GetRequiredService<IConfiguration>());
            Assert.True((await state.TransitionAsync(MigrationService.Catalog, new(MigrationStage.ShadowValidation, 0), flags, "test", "", CancellationToken.None)).Succeeded);
            Assert.True((await state.TransitionAsync(MigrationService.Catalog, new(MigrationStage.CanaryReady, 1), flags, "test", "", CancellationToken.None)).Succeeded);
            using var client = gateway.CreateClient();
            foreach (var key in new[] { "alpha", "beta", "gamma" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/api/products");
                request.Headers.Add("X-Migration-Key", key);
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(TrafficAssignment.IsModern(key, percentage) ? "modern" : "legacy", response.Headers.GetValues("X-Legacy").Single());
            }
            using var post = await client.PostAsJsonAsync("/api/products", new { name = "test", price = 1 });
            Assert.NotEqual(HttpStatusCode.Created, post.StatusCode);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public async Task ConflictingWriterRouteAndPauseFailClosed()
    {
        await using var legacy = await StartCatalogAsync("legacy", 12);
        await using var gateway = new WebApplicationFactory<GatewayProgram>().WithWebHostBuilder(host => host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReverseProxy:Clusters:legacy:Destinations:monolith:Address"] = legacy.Urls.Single(),
            ["ReverseProxy:Routes:catalog:ClusterId"] = "legacy",
            ["MigrationControl:EmergencyPause"] = "true"
        })));
        using var client = gateway.CreateClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/api/products", new { name = "test", price = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/api/orders", new { customerId = Guid.NewGuid(), productId = Guid.NewGuid(), quantity = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/internal/migration/status")).StatusCode);
    }
    [Fact]
    public void CatalogSchemaAndNormalizationCannotHideBusinessDifferences()
    {
        Assert.Null(ContractComparison.ValidateCatalogList("[{\"id\":\"00000000-0000-0000-0000-000000000201\",\"name\":\"Widget\",\"price\":12}]"));
        Assert.Equal("RequiredField", ContractComparison.ValidateCatalogList("[{\"id\":\"00000000-0000-0000-0000-000000000201\",\"price\":12}]"));
        var config = Configuration();
        config["MigrationControl:NormalizedFields:0"] = "price";
        Assert.Throws<InvalidOperationException>(() => MigrationFlagReader.Read(config));
    }
    [Fact]
    public async Task ModernCatalogListStreamsBeyondShadowComparisonLimit()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var catalog = builder.Build();
        var payload = new string('x', 300_000);
        catalog.MapGet("/api/products", () => Results.Text(payload));
        await catalog.StartAsync();
        await using var gateway = new WebApplicationFactory<GatewayProgram>().WithWebHostBuilder(host => host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReverseProxy:Clusters:catalog:Destinations:service:Address"] = catalog.Urls.Single()
        })));
        using var response = await gateway.CreateClient().GetAsync("/api/products");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(payload.Length, (await response.Content.ReadAsStringAsync()).Length);
    }

    [Fact]
    public async Task ShadowValidationKeepsLegacyAsPrimaryAtFullModernPercentage()
    {
        await using var legacy = await StartCatalogAsync("legacy", 12);
        await using var modern = await StartCatalogAsync("modern", 13);
        var stateFile = "migration-state-test-" + Guid.NewGuid().ToString("N") + ".json";
        await using var gateway = new WebApplicationFactory<GatewayProgram>().WithWebHostBuilder(host => host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MigrationControl:Catalog:Owner"] = "Legacy",
            ["MigrationControl:Catalog:ModernReadPercent"] = "100",
            ["MigrationControl:Catalog:LegacyReadAvailable"] = "true",
            ["MigrationControl:StateFile"] = stateFile,
            ["ReverseProxy:Clusters:legacy:Destinations:monolith:Address"] = legacy.Urls.Single(),
            ["ReverseProxy:Clusters:catalog:Destinations:service:Address"] = modern.Urls.Single(),
            ["ReverseProxy:Routes:catalog:ClusterId"] = "legacy"
        })));
        var path = Path.Combine(gateway.Server.Services.GetRequiredService<IHostEnvironment>().ContentRootPath, stateFile);
        try
        {
            var store = gateway.Services.GetRequiredService<MigrationStateStore>();
            var flags = MigrationFlagReader.Read(gateway.Services.GetRequiredService<IConfiguration>());
            Assert.True((await store.TransitionAsync(MigrationService.Catalog, new(MigrationStage.ShadowValidation, 0), flags, "test", "", CancellationToken.None)).Succeeded);
            using var response = await gateway.CreateClient().GetAsync("/api/products");
            Assert.Equal("legacy", response.Headers.GetValues("X-Legacy").Single());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public async Task CanaryTransitionRequiresAWriteFreeCheckpoint()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "migration-state-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = Configuration(DataOwner.Legacy);
            config["MigrationControl:Catalog:LegacyReadAvailable"] = "true";
            var states = new MigrationStateStore(new TestEnvironment(root), config);
            var flags = MigrationFlagReader.Read(config);
            Assert.True((await states.TransitionAsync(MigrationService.Catalog, new(MigrationStage.ShadowValidation, 0), flags, "test", "", CancellationToken.None)).Succeeded);
            Assert.False((await states.TransitionAsync(MigrationService.Catalog, new(MigrationStage.CanaryReady, 1), flags, "test", "", CancellationToken.None)).Succeeded);
            config["MigrationControl:Catalog:Checkpoint"] = "verified";
            config["MigrationControl:Catalog:WritesSinceCheckpoint"] = "false";
            Assert.True((await states.TransitionAsync(MigrationService.Catalog, new(MigrationStage.CanaryReady, 1), MigrationFlagReader.Read(config), "test", "", CancellationToken.None)).Succeeded);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void PreCutoverLegacyStateCanBeEligibleButModernStateCannot()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "migration-state-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = Configuration(DataOwner.Legacy);
            config["MigrationControl:Catalog:LegacyReadAvailable"] = "true";
            config["MigrationControl:Catalog:WritesSinceCheckpoint"] = "false";
            config["MigrationControl:Catalog:Checkpoint"] = "fixture-verified";
            var states = new MigrationStateStore(new TestEnvironment(root), config);
            Assert.True(states.Evaluate(MigrationService.Catalog, MigrationFlagReader.Read(config)).Eligible);
            config["MigrationControl:Catalog:Owner"] = "Modern";
            config["MigrationControl:Catalog:ModernReadPercent"] = "100";
            config["MigrationControl:Catalog:LegacyReadAvailable"] = "false";
            Assert.False(states.Evaluate(MigrationService.Catalog, MigrationFlagReader.Read(config)).Eligible);
        }
        finally { Directory.Delete(root, true); }
    }
}
