extern alias CatalogApi;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StranglerForge.Catalog.Domain;
using StranglerForge.Catalog.Infrastructure;

namespace LegacyCommerce.IntegrationTests;

public sealed class CatalogExtractionTests
{
    [Fact]
    public void DomainRejectsInvalidProducts()
    {
        Assert.Throws<ArgumentException>(() => CatalogProduct.Create(Guid.Empty, "Name", 1m));
        Assert.Throws<ArgumentException>(() => CatalogProduct.Create(Guid.NewGuid(), "", 1m));
        Assert.Throws<ArgumentException>(() => CatalogProduct.Create(Guid.NewGuid(), "Name", 0m));
    }

    [Fact]
    public async Task ExtractedCatalogPreservesCreationAndValidationContract()
    {
        var connection = Environment.GetEnvironmentVariable("CATALOG_TEST_CONNECTION")
            ?? throw new InvalidOperationException("CATALOG_TEST_CONNECTION must point to an isolated PostgreSQL test database.");
        await CatalogTestDatabase.WaitUntilAvailableAsync(connection);
        await using var catalogFactory = CreateCatalogFactory(connection, true);
        await using var legacyFactory = LegacyTestFactory.Create();
        using var catalogScope = catalogFactory.Services.CreateScope();
        using var legacyScope = legacyFactory.Services.CreateScope();
        var catalogDb = catalogScope.ServiceProvider.GetRequiredService<CatalogDb>();
        var legacyDb = legacyScope.ServiceProvider.GetRequiredService<CommerceDb>();
        await catalogDb.Database.MigrateAsync();
        var catalogClient = catalogFactory.CreateClient();
        var legacyClient = legacyFactory.CreateClient();
        var invalid = new { name = "", price = 0m };
        var catalogInvalid = await catalogClient.PostAsJsonAsync("/api/products", invalid);
        var legacyInvalid = await legacyClient.PostAsJsonAsync("/api/products", invalid);
        Assert.Equal(legacyInvalid.StatusCode, catalogInvalid.StatusCode);
        Assert.Equal(await legacyInvalid.Content.ReadAsStringAsync(), await catalogInvalid.Content.ReadAsStringAsync());
        var name = $"Contract Product {Guid.NewGuid():N}";
        var body = new { name, price = 13.75m };
        var seedId = Guid.Parse("00000000-0000-0000-0000-000000000201");
        var seedAdded = false;
        if (await catalogDb.Products.FindAsync(seedId) is null)
        {
            catalogDb.Products.Add(CatalogProduct.Create(seedId, "Sample Product", 19.99m));
            await catalogDb.SaveChangesAsync();
            seedAdded = true;
        }
        Guid legacyId = Guid.Empty;
        Guid catalogId = Guid.Empty;
        try
        {
            var legacyCreated = await legacyClient.PostAsJsonAsync("/api/products", body);
            var catalogCreated = await catalogClient.PostAsJsonAsync("/api/products", body);
            Assert.Equal(HttpStatusCode.Created, legacyCreated.StatusCode);
            Assert.Equal(legacyCreated.StatusCode, catalogCreated.StatusCode);
            using var legacyJson = JsonDocument.Parse(await legacyCreated.Content.ReadAsStringAsync());
            using var catalogJson = JsonDocument.Parse(await catalogCreated.Content.ReadAsStringAsync());
            legacyId = legacyJson.RootElement.GetProperty("id").GetGuid();
            catalogId = catalogJson.RootElement.GetProperty("id").GetGuid();
            Assert.Equal(legacyJson.RootElement.GetProperty("name").GetString(), catalogJson.RootElement.GetProperty("name").GetString());
            Assert.Equal(legacyJson.RootElement.GetProperty("price").GetDecimal(), catalogJson.RootElement.GetProperty("price").GetDecimal());
            Assert.Equal($"/api/products/{catalogId}", catalogCreated.Headers.Location?.ToString());
            var legacyList = await legacyClient.GetFromJsonAsync<JsonElement>("/api/products?source=contract");
            var catalogList = await catalogClient.GetFromJsonAsync<JsonElement>("/api/products?source=contract");
            var legacySeed = legacyList.EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == seedId);
            var catalogSeed = catalogList.EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == seedId);
            Assert.Equal(legacySeed.GetProperty("name").GetString(), catalogSeed.GetProperty("name").GetString());
            Assert.Equal(legacySeed.GetProperty("price").GetDecimal(), catalogSeed.GetProperty("price").GetDecimal());
            var listed = await catalogClient.GetFromJsonAsync<JsonElement>("/api/products?source=contract");
            Assert.Contains(listed.EnumerateArray(), product => product.GetProperty("id").GetGuid() == catalogId);
            var detail = await catalogClient.GetFromJsonAsync<JsonElement>($"/api/products/{catalogId}");
            Assert.Equal(catalogId, detail.GetProperty("id").GetGuid());
            Assert.Equal(HttpStatusCode.NotFound, (await catalogClient.GetAsync($"/api/products/{Guid.NewGuid()}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await catalogClient.GetAsync("/health")).StatusCode);
        }
        finally
        {
            if (legacyId != Guid.Empty)
                await legacyDb.Products.Where(product => product.Id == legacyId).ExecuteDeleteAsync();
            if (catalogId != Guid.Empty)
                await catalogDb.Products.Where(product => product.Id == catalogId).ExecuteDeleteAsync();
            if (seedAdded)
                await catalogDb.Products.Where(product => product.Id == seedId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task WritesRemainFrozenUntilCutover()
    {
        var connection = Environment.GetEnvironmentVariable("CATALOG_TEST_CONNECTION")
            ?? throw new InvalidOperationException("CATALOG_TEST_CONNECTION is required.");
        await using var factory = CreateCatalogFactory(connection, false);
        using var response = await factory.CreateClient().PostAsJsonAsync("/api/products", new { name = "Frozen", price = 1m });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private static WebApplicationFactory<CatalogApi::CatalogProgram> CreateCatalogFactory(string connection, bool writesEnabled) =>
        new WebApplicationFactory<CatalogApi::CatalogProgram>().WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Catalog", connection)
                .UseSetting("Catalog:WritesEnabled", writesEnabled.ToString()));
}
