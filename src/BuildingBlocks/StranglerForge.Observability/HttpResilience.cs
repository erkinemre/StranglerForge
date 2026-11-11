using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace StranglerForge.Observability;

public static class HttpResilience
{
    public static IHttpClientBuilder AddSafeReadResilience(this IHttpClientBuilder client, TimeSpan? breakDuration = null)
    {
        client.AddStandardResilienceHandler(options =>
        {
            options.Retry.DisableForUnsafeHttpMethods();
            options.Retry.MaxRetryAttempts = 2;
            options.Retry.Delay = TimeSpan.FromMilliseconds(150);
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.CircuitBreaker.MinimumThroughput = 10;
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
            options.CircuitBreaker.BreakDuration = breakDuration ?? TimeSpan.FromSeconds(10);
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(2);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(6);
        });
        return client;
    }
}
