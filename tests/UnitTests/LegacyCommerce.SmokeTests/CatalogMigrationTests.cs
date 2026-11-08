using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using StranglerForge.Catalog.Domain;
using StranglerForge.Catalog.Infrastructure;

[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace LegacyCommerce.IntegrationTests;

public sealed class CatalogMigrationTests
{
    [Fact]
    public async Task ImportIsIdempotentAndRejectsUnexpectedTargetRows()
    {
        var targetConnection = Environment.GetEnvironmentVariable("CATALOG_TEST_CONNECTION")
            ?? throw new InvalidOperationException("CATALOG_TEST_CONNECTION is required.");
        var sourceConnection = Environment.GetEnvironmentVariable("CATALOG_SOURCE_CONNECTION")
            ?? "Server=localhost;Database=StranglerForgeCatalogMigrationTests;Trusted_Connection=True;TrustServerCertificate=True";
        await CatalogTestDatabase.WaitUntilAvailableAsync(targetConnection);
        await using (var testSource = new CommerceDb(new DbContextOptionsBuilder<CommerceDb>().UseSqlServer(sourceConnection).Options))
            await testSource.Database.MigrateAsync();
        await using (var testTarget = new CatalogDb(new DbContextOptionsBuilder<CatalogDb>().UseNpgsql(targetConnection).Options))
        {
            await testTarget.Database.MigrateAsync();

        }
        var root = FindRepositoryRoot();
        var first = await RunMigrationAsync(root, sourceConnection, targetConnection, "--import");
        Assert.True(first.ExitCode == 0, first.Output);
        Assert.Contains("cutoverReady=true", first.Output);
        var second = await RunMigrationAsync(root, sourceConnection, targetConnection, "--import");
        Assert.True(second.ExitCode == 0, second.Output);
        Assert.Contains("created=0 updated=0", second.Output);
        var verified = await RunMigrationAsync(root, sourceConnection, targetConnection, "--verify");
        Assert.True(verified.ExitCode == 0, verified.Output);

        await using var source = new CommerceDb(new DbContextOptionsBuilder<CommerceDb>().UseSqlServer(sourceConnection).Options);
        await using var target = new CatalogDb(new DbContextOptionsBuilder<CatalogDb>().UseNpgsql(targetConnection).Options);
        var sourceIds = await source.Products.AsNoTracking().Select(product => product.Id).OrderBy(id => id).ToListAsync();
        var targetIds = await target.Products.AsNoTracking().Select(product => product.Id).OrderBy(id => id).ToListAsync();
        Assert.Equal(sourceIds, targetIds);
        var lateProduct = Product.Create($"Late referenced product {Guid.NewGuid():N}", 8m);
        source.Products.Add(lateProduct);
        source.Stock.Add(new StockItem { ProductId = lateProduct.Id, Quantity = 2 });
        await source.SaveChangesAsync();
        try
        {
            var appended = await RunMigrationAsync(root, sourceConnection, targetConnection, "--append-missing");
            Assert.Equal(0, appended.ExitCode);
            Assert.Contains("appended=1", appended.Output);
            Assert.Equal(lateProduct.Name, (await target.Products.AsNoTracking().SingleAsync(x => x.Id == lateProduct.Id)).Name);
            var repeated = await RunMigrationAsync(root, sourceConnection, targetConnection, "--append-missing");
            Assert.Equal(0, repeated.ExitCode);
            Assert.Contains("appended=0", repeated.Output);
        }
        finally
        {
            await target.Products.Where(x => x.Id == lateProduct.Id).ExecuteDeleteAsync();
            await source.Stock.Where(x => x.ProductId == lateProduct.Id).ExecuteDeleteAsync();
            await source.Products.Where(x => x.Id == lateProduct.Id).ExecuteDeleteAsync();
        }
        var extra = CatalogProduct.Create(Guid.NewGuid(), "Unexpected target row", 1m);
        target.Products.Add(extra);
        await target.SaveChangesAsync();
        try
        {
            var rejected = await RunMigrationAsync(root, sourceConnection, targetConnection, "--import");
            Assert.Equal(1, rejected.ExitCode);
            Assert.Contains("Target contains 1 IDs absent from source", rejected.Output);
            Assert.NotNull(await target.Products.FindAsync(extra.Id));
        }
        finally
        {
            await target.Products.Where(product => product.Id == extra.Id).ExecuteDeleteAsync();
        }
    }

    private static async Task<(int ExitCode, string Output)> RunMigrationAsync(
        string root, string source, string target, string mode)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "run", "--project",
            "src/Services/Catalog/Catalog.Migration/Catalog.Migration.csproj", "--", mode })
            start.ArgumentList.Add(argument);
        start.Environment["ConnectionStrings__LegacyCommerce"] = source;
        start.Environment["ConnectionStrings__Catalog"] = target;
        start.Environment["CATALOG_WRITE_FREEZE_ACK"] = "true";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start migration tool.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "StranglerForge.slnx")))
            current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("Solution root not found.");
    }
}
