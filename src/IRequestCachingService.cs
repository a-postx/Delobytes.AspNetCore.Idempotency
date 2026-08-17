
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Delobytes.AspNetCore.Idempotency;

/// <summary>
/// Сервис кеширования запросов.
/// </summary>
public interface IRequestCachingService
{
    /// <summary>
    /// Достать запрос из кеша либо закешировать текущий, если его ещё нет.
    /// </summary>
    public Task<(bool created, ApiRequest? request)> GetOrCreateRequestAsync(HttpContext ctx,
        string idempotencyKey,
        string cacheKey,
        string method,
        string? path,
        string? query);
    /// <summary>
    /// Получить запрос из кеша.
    /// </summary>
    public Task<ApiRequest?> GetRequestFromCacheAsync(string cacheKey, CancellationToken cancellationToken);
    /// <summary>
    /// Закешировать запрос.
    /// </summary>
    public Task<bool> CacheRequestAsync(string cacheKey, ApiRequest apiRequest, CancellationToken cancellationToken);
    /// <summary>
    /// Добавить ответ в запрос из кеша.
    /// </summary>
    public Task<bool> SetResponseInCacheAsync(string key, ApiRequest apiRequest, CancellationToken cancellationToken);
}
