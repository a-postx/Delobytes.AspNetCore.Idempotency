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
public class RequestCachingService : IRequestCachingService
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
        (bool success, byte[]? cachedRequest) = await ReadCacheAsync(cacheKey, ctx.RequestAborted);

        if (success && cachedRequest is not null && cachedRequest.Length > 0)
        {
            try
            {
                ApiRequest? requestFromCache = JsonSerializer.Deserialize<ApiRequest>(cachedRequest, _serializerOptions);
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
        (bool success, byte[]? request) = await ReadCacheAsync(cacheKey, cancellationToken);

        if (success == false || request is null || request.Length == 0)
        {
            return null;
        }

        try
        {
            ApiRequest? requestFromCache = JsonSerializer.Deserialize<ApiRequest>(request, _serializerOptions);
            return requestFromCache;
        }
        catch (JsonException ex)
        {
            _log.LogError(ex, "Error deserializing cached request value for key {CacheKey}", cacheKey);
            return null;
        }
    }

    private async Task<(bool success, byte[]? request)> ReadCacheAsync(string cacheKey, CancellationToken cancellationToken)
    {
        byte[]? cachedApiRequest;
        DateTime startGetDt = DateTime.UtcNow;

        try
        {
            if (_options.CacheRequestTimeoutMs > 0)
            {
                using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    cts.CancelAfter(_options.CacheRequestTimeoutMs);

                    cachedApiRequest = await _distributedCache.GetAsync(cacheKey, cts.Token);
                }
            }
            else
            {
                cachedApiRequest = await _distributedCache.GetAsync(cacheKey, cancellationToken);
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

        return (true, cachedApiRequest);
    }

    /// <summary>
    /// Закешировать запрос.
    /// </summary>
    public async Task<bool> CacheRequestAsync(string cacheKey, ApiRequest apiRequest, CancellationToken cancellationToken)
    {
        byte[] serializedRequest = SerializeRequest(apiRequest);

        DateTime startSetDt = DateTime.UtcNow;

        try
        {
            await SetCacheAsync(cacheKey, serializedRequest, cancellationToken);
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
    public async Task<bool> SetResponseInCacheAsync(string cacheKey, ApiRequest apiRequest, CancellationToken cancellationToken)
    {
        byte[] serializedRequest = SerializeRequest(apiRequest);

        DateTime startSetDt = DateTime.UtcNow;

        try
        {
            await SetCacheAsync(cacheKey, serializedRequest, cancellationToken);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error updating cached value for key {CacheKey}", cacheKey);
            return false;
        }
        finally
        {
            TimeSpan processingTime = DateTime.UtcNow - startSetDt;
            _log.LogInformation("cache.request.idempotency.update.msec {CacheRequestIdempotencyUpdateMsec}", (int)processingTime.TotalMilliseconds);
        }

        return true;
    }

    private byte[] SerializeRequest(ApiRequest apiRequest)
    {
        if (apiRequest.Body is not null && _options.MaxBodySizeBytes > 0)
        {
            if (apiRequest.Body.Length > _options.MaxBodySizeBytes)
            {
                apiRequest.Body = null;
            }
        }

        byte[] serializedRequest = JsonSerializer.SerializeToUtf8Bytes(apiRequest, _serializerOptions);

        return serializedRequest;
    }

    private async Task SetCacheAsync(string cacheKey, byte[] serializedRequest, CancellationToken cancellationToken)
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
}
