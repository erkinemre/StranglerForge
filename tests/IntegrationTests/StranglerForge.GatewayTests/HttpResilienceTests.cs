using Polly.CircuitBreaker;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using StranglerForge.Observability;

namespace StranglerForge.GatewayTests;

public sealed class HttpResilienceTests
{
    [Fact]
    public async Task TransientGetIsRetriedWithinTheBoundedPolicy()
    {
        var calls = 0;
        using var services = new ServiceCollection()
            .AddHttpClient("safe-read")
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(() => Interlocked.Increment(ref calls) == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK))
            .AddSafeReadResilience()
            .Services.BuildServiceProvider();
        using var client = services.GetRequiredService<IHttpClientFactory>().CreateClient("safe-read");
        using var response = await client.GetAsync("http://example.test/products");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task MutatingPostIsNeverAutomaticallyRetried()
    {
        var calls = 0;
        using var services = new ServiceCollection()
            .AddHttpClient("mutation")
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(() => { Interlocked.Increment(ref calls); return HttpStatusCode.ServiceUnavailable; }))
            .AddSafeReadResilience()
            .Services.BuildServiceProvider();
        using var client = services.GetRequiredService<IHttpClientFactory>().CreateClient("mutation");
        using var response = await client.PostAsync("http://example.test/reservations", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CircuitOpensAfterTransientFailuresAndRecoversAfterBreak()
    {
        var calls = 0;
        using var services = new ServiceCollection()
            .AddHttpClient("circuit")
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(() => Interlocked.Increment(ref calls) <= 10 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK))
            .AddSafeReadResilience(TimeSpan.FromSeconds(1))
            .Services.BuildServiceProvider();
        using var client = services.GetRequiredService<IHttpClientFactory>().CreateClient("circuit");
        for (var index = 0; index < 10; index++)
        {
            using var failed = await client.PostAsync("http://example.test/reservations", new StringContent("{}"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        }
        await Assert.ThrowsAsync<BrokenCircuitException>(() => client.PostAsync("http://example.test/reservations", new StringContent("{}")));
        Assert.Equal(10, calls);
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        using var recovered = await client.PostAsync("http://example.test/reservations", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(11, calls);
    }
    private sealed class StubHandler(Func<HttpStatusCode> next) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(next()));
    }
}
