using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using TaskManager.Application.Interfaces;

namespace TaskManager.Infrastructure.Caching;

/// <summary>Records which cache backend is active ("InMemory" or "Redis") for diagnostics.</summary>
public sealed record CacheBackendInfo(string Backend);

/// <summary>Cache-aside helper for read-heavy per-user lookups (categories, tags).</summary>
public class LookupCache : ILookupCache
{
    private readonly IDistributedCache _cache;
    private static readonly DistributedCacheEntryOptions Options = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
    };

    public LookupCache(IDistributedCache cache) => _cache = cache;

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct)
    {
        try
        {
            var bytes = await _cache.GetAsync(key, ct);
            return bytes is null ? default : JsonSerializer.Deserialize<T>(bytes);
        }
        catch
        {
            return default;
        }
    }

    public async Task SetAsync<T>(string key, T value, CancellationToken ct)
    {
        try
        {
            await _cache.SetAsync(key, JsonSerializer.SerializeToUtf8Bytes(value), Options, ct);
        }
        catch
        {
            /* cache is best-effort */
        }
    }

    public async Task RemoveAsync(string key, CancellationToken ct)
    {
        try
        {
            await _cache.RemoveAsync(key, ct);
        }
        catch
        {
            /* cache is best-effort */
        }
    }
}
