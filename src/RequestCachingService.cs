using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Delobytes.AspNetCore.Idempotency;

/// <summary>
/// Сервис кеширования запросов.
/// </summary>
public class RequestCachingService
{
    /// <summary>
    /// Конструктор.
    /// </summary>
    public RequestCachingService(ILogger<RequestCachingService> logger,
        IOptions<IdempotencyControlOptions> options,
        IDistributedCache distributedCache,
        JsonSerializerOptions serializerOptions)
    {
        _log = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value;
        _distributedCache = distributedCache ?? throw new ArgumentNullException(nameof(distributedCache));
        _serializerOptions = serializerOptions ?? throw new ArgumentNullException(nameof(serializerOptions));
    }

    private readonly ILogger<RequestCachingService> _log;
    private readonly IdempotencyControlOptions _options;
    private readonly IDistributedCache _distributedCache;
    private readonly JsonSerializerOptions _serializerOptions;

    /// <summary>
    /// Достать запрос из кеша либо закешировать текущий, если его ещё нет.
    /// </summary>
    public async Task<(bool created, ApiRequest? request)> GetOrCreateRequestAsync(HttpContext ctx,
        string idempotencyKey,
        string cacheKey,
        string method,
        string? path,
        string? query)
    {
        byte[]? cachedApiRequest;

        DateTime startGetDt = DateTime.UtcNow;

        try
        {
            if (_options.CacheRequestTimeoutMs > 0)
            {
                using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted))
                {
                    cts.CancelAfter(_options.CacheRequestTimeoutMs);

                    cachedApiRequest = await _distributedCache.GetAsync(cacheKey, cts.Token);
                }
            }
            else
            {
                cachedApiRequest = await _distributedCache.GetAsync(cacheKey, ctx.RequestAborted);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error getting cached value for key {CacheKey}", cacheKey);
            return (false, null);
        }
        finally
        {
            TimeSpan processingTime = DateTime.UtcNow - startGetDt;
            _log.LogInformation("cache.request.idempotency.get.msec {CacheRequestIdempotencyGetMsec}", (int)processingTime.TotalMilliseconds);
        }

        if (cachedApiRequest is not null && cachedApiRequest.Length > 0)
        {
            try
            {
                ApiRequest? requestFromCache = JsonSerializer.Deserialize<ApiRequest>(cachedApiRequest, _serializerOptions);
                return (false, requestFromCache);
            }
            catch (JsonException ex)
            {
                _log.LogError(ex, "Error deserializing cached value for key {CacheKey}", cacheKey);
                return (false, null);
            }
        }

        ApiRequest newRequest = new ApiRequest(idempotencyKey, method);
        newRequest.Status = IdempotencyRequestStatus.InProgress;
        newRequest.Path = path;
        newRequest.Query = query;

        bool requestCached = await CacheRequestAsync(cacheKey, newRequest, ctx.RequestAborted);

        if (!requestCached)
        {
            if (!_options.Optional)
            {
                throw new IdempotencyException("Error creating cached request");
            }

            return (false, null);
        }

        return (true, newRequest);
    }

    /// <summary>
    /// Получить запрос из кеша.
    /// </summary>
    public async Task<ApiRequest?> GetRequestFromCacheAsync(string cacheKey, CancellationToken cancellationToken)
    {
        byte[]? cachedRequest;

        DateTime startGetDt = DateTime.UtcNow;

        try
        {
            if (_options.CacheRequestTimeoutMs > 0)
            {
                using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    cts.CancelAfter(_options.CacheRequestTimeoutMs);

                    cachedRequest = await _distributedCache.GetAsync(cacheKey, cts.Token);
                }
            }
            else
            {
                cachedRequest = await _distributedCache.GetAsync(cacheKey, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error getting cached value for key {CacheKey}", cacheKey);
            return null;
        }
        finally
        {
            TimeSpan processingTime = DateTime.UtcNow - startGetDt;
            _log.LogInformation("cache.request.idempotency.get.msec {CacheRequestIdempotencyGetMsec}", (int)processingTime.TotalMilliseconds);
        }

        if (cachedRequest is null || cachedRequest.Length == 0)
        {
            return null;
        }

        try
        {
            ApiRequest? requestFromCache = JsonSerializer.Deserialize<ApiRequest>(cachedRequest, _serializerOptions);
            return requestFromCache;
        }
        catch (JsonException ex)
        {
            _log.LogError(ex, "Error deserializing cached request value for key {CacheKey}", cacheKey);
            return null;
        }
    }

    /// <summary>
    /// Закешировать запрос.
    /// </summary>
    public async Task<bool> CacheRequestAsync(string cacheKey, ApiRequest apiRequest, CancellationToken cancellationToken)
    {
        byte[] serializedRequest = JsonSerializer.SerializeToUtf8Bytes(apiRequest, _serializerOptions);

        DateTime startSetDt = DateTime.UtcNow;

        try
        {
            DistributedCacheEntryOptions cacheOpts = new DistributedCacheEntryOptions
            {
                AbsoluteExpiration = DateTimeOffset.UtcNow.AddHours(_options.CacheAbsoluteExpirationHrs)
            };

            if (_options.CacheRequestTimeoutMs > 0)
            {
                using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    cts.CancelAfter(_options.CacheRequestTimeoutMs);

                    await _distributedCache.SetAsync(cacheKey, serializedRequest, cacheOpts, cts.Token);
                }
            }
            else
            {
                await _distributedCache.SetAsync(cacheKey, serializedRequest, cacheOpts, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error creating cached value for key {CacheKey}", cacheKey);
            return false;
        }
        finally
        {
            TimeSpan processingTime = DateTime.UtcNow - startSetDt;
            _log.LogInformation("cache.request.idempotency.create.msec {CacheRequestIdempotencyCreateMsec}", (int)processingTime.TotalMilliseconds);
        }

        return true;
    }

    /// <summary>
    /// Добавить ответ в запрос из кеша.
    /// </summary>
    public async Task<bool> SetResponseInCacheAsync(string key, ApiRequest apiRequest, CancellationToken cancellationToken)
    {
        byte[] serializedRequest = JsonSerializer.SerializeToUtf8Bytes(apiRequest, _serializerOptions);

        DateTime startSetDt = DateTime.UtcNow;

        try
        {
            DistributedCacheEntryOptions cacheOpts = new DistributedCacheEntryOptions
            {
                AbsoluteExpiration = DateTimeOffset.UtcNow.AddHours(_options.CacheAbsoluteExpirationHrs)
            };

            if (_options.CacheRequestTimeoutMs > 0)
            {
                using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    cts.CancelAfter(_options.CacheRequestTimeoutMs);

                    await _distributedCache.SetAsync(key, serializedRequest, cacheOpts, cts.Token);
                }
            }
            else
            {
                await _distributedCache.SetAsync(key, serializedRequest, cacheOpts, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error updating cached value for key {CacheKey}", key);
            return false;
        }
        finally
        {
            TimeSpan processingTime = DateTime.UtcNow - startSetDt;
            _log.LogInformation("cache.request.idempotency.update.msec {CacheRequestIdempotencyUpdateMsec}", (int)processingTime.TotalMilliseconds);
        }

        return true;
    }
}
