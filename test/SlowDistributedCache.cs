using System.Threading;
using Microsoft.Extensions.Caching.Distributed;

namespace Delobytes.AspNetCore.Idempotency.Tests;

public class SlowDistributedCache : IDistributedCache
{
    public byte[] Get(string key)
    {
        throw new NotImplementedException();
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        await Task.Delay(4000, token);
        throw new NotImplementedException();
    }

    public void Refresh(string key)
    {
        throw new NotImplementedException();
    }

    public Task RefreshAsync(string key, CancellationToken token = default)
    {
        throw new NotImplementedException();
    }

    public void Remove(string key)
    {
        throw new NotImplementedException();
    }

    public Task RemoveAsync(string key, CancellationToken token = default)
    {
        throw new NotImplementedException();
    }

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        throw new NotImplementedException();
    }

    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        throw new NotImplementedException();
    }
}
