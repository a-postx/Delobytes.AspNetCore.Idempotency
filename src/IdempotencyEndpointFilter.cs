using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Delobytes.AspNetCore.Idempotency;

/// <summary>
/// Фильтр идемпотентности: сохраняет результаты запроса с ключом идемпотентности в кеш,
/// чтобы вернуть тот же ответ в случае запроса-дубликата.
/// Реализация по примеру https://stripe.com/docs/api/idempotent_requests
/// </summary>
public class IdempotencyEndpointFilter<T> : IEndpointFilter where T : class
{
    /// <summary>
    /// Конструктор.
    /// </summary>
    public IdempotencyEndpointFilter(ILogger<IdempotencyEndpointFilter<T>> logger,
        IOptions<IdempotencyControlOptions> options,
        IDistributedCache distributedCache,
        JsonSerializerOptions serializerOptions)
    {
        _log = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value;
        _distributedCache = distributedCache ?? throw new ArgumentNullException(nameof(distributedCache));
        _serializerOptions = serializerOptions ?? throw new ArgumentNullException(nameof(serializerOptions));
        _cacheEntryOptions = new DistributedCacheEntryOptions
        {
            AbsoluteExpiration = DateTimeOffset.UtcNow.AddHours(_options.CacheAbsoluteExpirationHrs)
        };
    }

    private readonly ILogger<IdempotencyEndpointFilter<T>> _log;
    private readonly IdempotencyControlOptions _options;
    private readonly IDistributedCache _distributedCache;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly DistributedCacheEntryOptions _cacheEntryOptions;

    /// <summary>
    /// Проверяет идемпотентность и возвращает результат запроса из кеша если он уже был выполнен.
    /// </summary>
    /// <param name="context"></param>
    /// <param name="next"></param>
    /// <returns></returns>
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!_options.Enabled)
        {
            return await next.Invoke(context);
        }

        ////T? param = (T?)context.Arguments.FirstOrDefault(x => x?.GetType() == typeof(T));

        string idempotencyKey = GetIdempotencyKeyHeaderValue(context.HttpContext);

        if (string.IsNullOrEmpty(idempotencyKey))
        {
            if (_options.HeaderRequired)
            {
                return TypedResults.BadRequest($"Запрос не содержит заголовка {_options.IdempotencyHeader} или значение в нём неверно.");
            }
            else
            {
                return await next.Invoke(context);
            }
        }

        string cacheKey = $"{_options.CacheKeysPrefix}:{idempotencyKey}";

        string method = context.HttpContext.Request.Method;
        string? path = context.HttpContext.Request.Path.HasValue ? context.HttpContext.Request.Path.Value : null;
        string? query = context.HttpContext.Request.QueryString.HasValue ? context.HttpContext.Request.QueryString.ToUriComponent() : null;

        ApiRequest? cachedRequest = await GetRequestFromCacheAsync(cacheKey, context.HttpContext.RequestAborted);

        if (cachedRequest is null)
        {
            ApiRequest newRequest = new ApiRequest(idempotencyKey, method);
            newRequest.Path = path;
            newRequest.Query = query;

            bool requestCached = await CacheRequestAsync(cacheKey, newRequest, context.HttpContext.RequestAborted);

            if (requestCached is false && !_options.Optional)
            {
                throw new IdempotencyException("Error creating cached request");
            }

            object? executedContext = await next.Invoke(context);

            await UpdateRequestWithResponseDataAsync(context, executedContext, newRequest, cacheKey);

            return executedContext;
        }
        else
        {
            if (string.IsNullOrEmpty(cachedRequest.ResultType)) //выпало исключение?
            {
                _log.LogInformation("There is no cached response data for the request");
                return TypedResults.StatusCode(500);
            }

            Type? contextResultType = Type.GetType(cachedRequest.ResultType);

            if (contextResultType == null)
            {
                throw new IdempotencyException("Error getting cached request: unknown result type");
            }

            if (method != cachedRequest.Method || path != cachedRequest.Path || query != cachedRequest.Query)
            {
                _log.LogInformation("Idempotency cache already contains {ApiRequestID} and its properties are different from the current request", cachedRequest.ApiRequestID);
                return TypedResults.BadRequest($"В кеше исполнения уже есть запрос с идентификатором идемпотентности {cachedRequest.ApiRequestID} и его параметры отличны от текущего запроса.");
            }

            return await GetCachedResultAsync(context, cachedRequest, context.HttpContext.RequestAborted);
        }
    }

    private async Task<ApiRequest?> GetRequestFromCacheAsync(string cacheKey, CancellationToken cancellationToken)
    {
        string? cachedRequest;

        DateTime startGetDt = DateTime.UtcNow;

        try
        {
            if (_options.CacheRequestTimeoutMs > 0)
            {
                using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    cts.CancelAfter(_options.CacheRequestTimeoutMs);

                    cachedRequest = await _distributedCache.GetStringAsync(cacheKey, cts.Token);
                }
            }
            else
            {
                cachedRequest = await _distributedCache.GetStringAsync(cacheKey, cancellationToken);
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

        ApiRequest? requestFromCache;

        try
        {
            requestFromCache = JsonSerializer.Deserialize<ApiRequest>(cachedRequest, _serializerOptions);
        }
        catch (JsonException ex)
        {
            _log.LogError(ex, "Error deserializing cached request value for key {CacheKey}", cacheKey);
            return null;
        }

        return requestFromCache;
    }

    private async Task<bool> CacheRequestAsync(string cacheKey, ApiRequest apiRequest, CancellationToken cancellationToken)
    {
        string serializedRequest = JsonSerializer.Serialize(apiRequest, _serializerOptions);

        DateTime startSetDt = DateTime.UtcNow;

        try
        {
            if (_options.CacheRequestTimeoutMs > 0)
            {
                using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    cts.CancelAfter(_options.CacheRequestTimeoutMs);

                    await _distributedCache.SetStringAsync(cacheKey, serializedRequest, _cacheEntryOptions, cts.Token);
                }
            }
            else
            {
                await _distributedCache.SetStringAsync(cacheKey, serializedRequest, _cacheEntryOptions, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error adding cached value for key {CacheKey}", cacheKey);
            return false;
        }
        finally
        {
            TimeSpan processingTime = DateTime.UtcNow - startSetDt;
            _log.LogInformation("cache.request.idempotency.create.msec {CacheRequestIdempotencyCreateMsec}", (int)processingTime.TotalMilliseconds);
        }

        return true;
    }

    private async Task<IResult> GetCachedResultAsync(EndpointFilterInvocationContext context, ApiRequest request, CancellationToken cancellationToken)
    {
        if (request.Headers != null)
        {
            foreach (KeyValuePair<string, List<string?>> item in request.Headers)
            {
                string headerValue = string.Join(";", item.Value);
                context.HttpContext.Response.Headers[item.Key] = headerValue;
            }
        }

        Type? resultType = Type.GetType(request.ResultType!);

        if (resultType == null)
        {
            throw new IdempotencyException("Cannot get result type.");
        }

        _log.LogInformation("Cached response returned from IdempotencyFilter.");

        if (resultType == typeof(StatusCodeHttpResult))
        {
            return TypedResults.StatusCode(request.StatusCode ?? 0);
        }
        else if (resultType == typeof(Ok))
        {
            return TypedResults.Ok();
        }
        else if (resultType == typeof(Created))
        {
            if (request.Location is null)
            {
                throw new IdempotencyException("Location URL cannot be found");
            }

            return TypedResults.Created(request.Location);
        }
        else if (resultType == typeof(Accepted))
        {
            return TypedResults.Accepted(request.Location);
        }
        else if (resultType == typeof(NotFound))
        {
            return TypedResults.NotFound();
        }
        else if (resultType == typeof(UnauthorizedHttpResult))
        {
            return TypedResults.Unauthorized();
        }
        else if (resultType == typeof(UnprocessableEntity))
        {
            return TypedResults.UnprocessableEntity();
        }
        else if (resultType == typeof(ProblemHttpResult))
        {
            return TypedResults.Problem();
        }
        else if (resultType == typeof(BadRequest))
        {
            return TypedResults.BadRequest();
        }
        else if (resultType == typeof(NotFound))
        {
            return TypedResults.NotFound();
        }
        else if (resultType == typeof(UnauthorizedHttpResult))
        {
            return TypedResults.Unauthorized();
        }
        else if (resultType == typeof(ForbidHttpResult))
        {
            return TypedResults.Forbid();
        }
        else if (resultType == typeof(Conflict))
        {
            return TypedResults.Conflict();
        }
        else if (resultType == typeof(NoContent))
        {
            return TypedResults.NoContent();
        }

        object? bodyObject = GetBodyObject(request);

        if (bodyObject != null)
        {
            if (resultType == typeof(Ok<T>))
            {
                return TypedResults.Ok(bodyObject as T);
            }

            if (resultType == typeof(CreatedAtRoute<T>))
            {
                return TypedResults
                    .CreatedAtRoute(bodyObject as T, request.ResultRouteName, request.ResultRouteValues);
            }

            if (resultType == typeof(AcceptedAtRoute<T>))
            {
                return TypedResults
                    .AcceptedAtRoute(bodyObject as T, request.ResultRouteName, request.ResultRouteValues);
            }

            if (resultType == typeof(BadRequest<ProblemDetails>))
            {
                return TypedResults.BadRequest(bodyObject as ProblemDetails);
            }

            if (resultType == typeof(BadRequest<string>))
            {
                return TypedResults.BadRequest(bodyObject as string);
            }
        }

        if (resultType.GetInterface(nameof(IStatusCodeHttpResult)) != null)
        {
            context.HttpContext.Response.StatusCode = request.StatusCode ?? 0;
        }

        if (resultType.GetInterface(nameof(IValueHttpResult<T>)) != null && bodyObject != null)
        {
            await context.HttpContext.Response.WriteAsJsonAsync(bodyObject, cancellationToken);
        }

        return TypedResults.Empty;
    }

    private async Task UpdateRequestWithResponseDataAsync(EndpointFilterInvocationContext ctx,
        object? executedContext, ApiRequest request, string cacheKey)
    {
        request.Headers = ctx
            .HttpContext.Response.Headers.ToDictionary(h => h.Key, h => h.Value.ToList());

        Type? contextType = executedContext?.GetType();

        if (contextType == null)
        {
            throw new IdempotencyException("Unknown result context type");
        }

        if (executedContext is IStatusCodeHttpResult scResult)
        {
            request.StatusCode = scResult.StatusCode;
        }

        object? resultObject = null;

        if (executedContext is INestedHttpResult iresultCtx && iresultCtx.Result != null)
        {
            //Result содержит OK<T>
            resultObject = iresultCtx.Result;
        }
        else if (executedContext is IValueHttpResult valueResult)
        {
            //.Value содержит T
            resultObject = valueResult;
        }

        if (resultObject is not null)
        {
            if (resultObject is IStatusCodeHttpResult objScResult)
            {
                request.StatusCode = objScResult.StatusCode;
            }

            Type resultType = resultObject.GetType();
            request.ResultType = resultType.AssemblyQualifiedName;

            if (resultType.GenericTypeArguments.Length > 0)
            {
                ////Type bodyType = resultType.GenericTypeArguments[0];

                if (resultObject is IValueHttpResult<T> typedResult)
                {
                    ////Type? tp = typedResult.Value?.GetType();

                    request.BodyType = GetBodyTypeName(typedResult);
                    request.Body = GetSerializedBody(typedResult);

                    if (resultObject is CreatedAtRoute<T> createdRequestResult)
                    {
                        request.ResultRouteName = createdRequestResult.RouteName;

                        Dictionary<string, string?>? routeValues = createdRequestResult
                            .RouteValues?.ToDictionary(r => r.Key, r => r.Value?.ToString());
                        request.ResultRouteValues = routeValues;
                    }
                }
                else if (resultObject is IValueHttpResult<string> stringResult)
                {
                    request.BodyType = GetBodyTypeName(stringResult);
                    request.Body = GetSerializedBody(stringResult);
                }
                else if (resultObject is IValueHttpResult<ProblemDetails> pdResult)
                {
                    request.BodyType = GetBodyTypeName(pdResult);
                    request.Body = GetSerializedBody(pdResult);
                }
                else
                {
                    throw new IdempotencyException($"Idempotency is not implemented for result type {resultObject.GetType()}");
                }
            }
            else
            {
                if (contextType == typeof(Created) || contextType == typeof(Accepted))
                {
                    object? locationProp = contextType.GetProperty("Location")?.GetValue(resultObject);

                    if (locationProp is not null && locationProp is string lp)
                    {
                        request.Location = lp;
                    }
                }

                request.BodyType = resultType.AssemblyQualifiedName;
                request.Body = JsonSerializer.SerializeToUtf8Bytes(resultObject, _serializerOptions);
            }
        }

        bool requestUpdatedSuccessfully = await SetResponseInCacheAsync(cacheKey, request, ctx.HttpContext.RequestAborted);

        if (!requestUpdatedSuccessfully)
        {
            throw new IdempotencyException("Failed to update request response.");
        }
    }

    private string? GetBodyTypeName<TResult>(IValueHttpResult<TResult> result) where TResult : class
    {
        return result.Value?.GetType().AssemblyQualifiedName;
    }

    private byte[]? GetSerializedBody<TResult>(IValueHttpResult<TResult> result) where TResult : class
    {
        return JsonSerializer.SerializeToUtf8Bytes(result.Value, _serializerOptions);
    }

    private async Task<bool> SetResponseInCacheAsync(string cacheKey, ApiRequest apiRequest, CancellationToken cancellationToken)
    {
        string serializedRequest = JsonSerializer.Serialize(apiRequest, _serializerOptions);

        DateTime startSetDt = DateTime.UtcNow;

        try
        {
            if (_options.CacheRequestTimeoutMs > 0)
            {
                using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    cts.CancelAfter(_options.CacheRequestTimeoutMs);

                    await _distributedCache.SetStringAsync(cacheKey, serializedRequest, _cacheEntryOptions, cts.Token);
                }
            }
            else
            {
                await _distributedCache.SetStringAsync(cacheKey, serializedRequest, _cacheEntryOptions, CancellationToken.None);
            }
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

    private string GetIdempotencyKeyHeaderValue(HttpContext httpContext)
    {
        return httpContext.Request.Headers
            .TryGetValue(_options.IdempotencyHeader, out StringValues idempotencyKeyValue)
            ? idempotencyKeyValue.ToString()
            : string.Empty;
    }

    private object? GetBodyObject(ApiRequest request)
    {
        if (request.BodyType == null)
        {
            throw new IdempotencyException("Body type not found");
        }

        Type? bodyType = Type.GetType(request.BodyType);

        if (bodyType == null)
        {
            throw new IdempotencyException($"Type for {request.BodyType} is not found");
        }

        object? bodyObject = null;

        try
        {
            bodyObject = JsonSerializer.Deserialize(request.Body, bodyType, _serializerOptions);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error deserializing body object");
        }

        return bodyObject;
    }
}
