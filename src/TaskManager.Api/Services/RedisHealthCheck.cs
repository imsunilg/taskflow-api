using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TaskManager.Api.Services;

public class RedisHealthCheck : IHealthCheck
{
    private readonly IDistributedCache _cache;

    public RedisHealthCheck(IDistributedCache cache) => _cache = cache;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _cache.GetAsync("__health__", cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Redis is not reachable.", ex);
        }
    }
}
