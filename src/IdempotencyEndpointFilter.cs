using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Delobytes.AspNetCore.Idempotency;

/// <summary>
/// Фильтр идемпотентности: сохраняет результаты запросов с ключом идемпотентности в кэш,
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
        RequestCachingService idempotencyService,
        JsonSerializerOptions serializerOptions)
    {
        _log = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value;
        _idempotencyService = idempotencyService ?? throw new ArgumentNullException(nameof(idempotencyService));
        _serializerOptions = serializerOptions ?? throw new ArgumentNullException(nameof(serializerOptions));
    }

    private readonly ILogger<IdempotencyEndpointFilter<T>> _log;
    private readonly IdempotencyControlOptions _options;
    private readonly RequestCachingService _idempotencyService;
    private readonly JsonSerializerOptions _serializerOptions;

    /// <summary>
    /// Проверяет идемпотентность и возвращает результат запроса из кэша если он уже был выполнен.
    /// </summary>
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!_options.Enabled)
        {
            return await next.Invoke(context);
        }

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

        ApiRequest? cachedRequest = await _idempotencyService.GetRequestFromCacheAsync(cacheKey, context.HttpContext.RequestAborted);

        if (cachedRequest is null)
        {
            ApiRequest newRequest = new ApiRequest(idempotencyKey, method);
            newRequest.Status = IdempotencyRequestStatus.InProgress;
            newRequest.Path = path;
            newRequest.Query = query;

            bool requestCached = await _idempotencyService.CacheRequestAsync(cacheKey, newRequest, context.HttpContext.RequestAborted);

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
            if (cachedRequest.Status == IdempotencyRequestStatus.InProgress)
            {
                return TypedResults.StatusCode(425);
            }

            switch (cachedRequest.Status)
            {
                case IdempotencyRequestStatus.InProgress:
                    return TypedResults.StatusCode(425);

                case IdempotencyRequestStatus.Completed:
                    if (cachedRequest.ResultKind == CachedResultKind.Unknown)
                    {
                        _log.LogInformation("There is no cached response data for the request");
                        return TypedResults.StatusCode(500);
                    }

                    if (method != cachedRequest.Method || path != cachedRequest.Path || query != cachedRequest.Query)
                    {
                        _log.LogInformation("Idempotency cache already contains {ApiRequestID} and its properties are different from the current request", cachedRequest.ApiRequestID);
                        return TypedResults.Conflict($"В кеше исполнения уже есть запрос с идентификатором {cachedRequest.ApiRequestID} и его параметры отличаются от текущего запроса.");
                    }

                    return GetCachedResult(context, cachedRequest);

                case IdempotencyRequestStatus.Unknown: //выпало исключение?
                    return TypedResults.StatusCode(500);

                default:
                    throw new IdempotencyException($"Unexpected request status: {cachedRequest.Status}");
            }
        }
    }

    private object? GetCachedResult(EndpointFilterInvocationContext context, ApiRequest request)
    {
        if (request.Headers != null)
        {
            foreach (KeyValuePair<string, List<string?>> item in request.Headers)
            {
                string headerValue = string.Join(";", item.Value);
                context.HttpContext.Response.Headers[item.Key] = headerValue;
            }
        }

        _log.LogInformation("Cached response returned from IdempotencyFilter.");

        switch (request.ResultKind)
        {
            case CachedResultKind.StatusCodeOnlyMinimalApi:
                return TypedResults.StatusCode(request.StatusCode ?? 0);

            case CachedResultKind.Ok:
                return TypedResults.Ok();

            case CachedResultKind.Created:
                if (request.Location is null)
                {
                    throw new IdempotencyException("Location URL cannot be found");
                }
                return TypedResults.Created(request.Location);

            case CachedResultKind.Accepted:
                return TypedResults.Accepted(request.Location);

            case CachedResultKind.NotFound:
                return TypedResults.NotFound();

            case CachedResultKind.Unauthorized:
                return TypedResults.Unauthorized();

            case CachedResultKind.UnprocessableEntity:
                return TypedResults.UnprocessableEntity();

            case CachedResultKind.Problem:
                return TypedResults.Problem();

            case CachedResultKind.BadRequest:
            {
                string? bodyObject = GetBodyObject<string>(request);
                return TypedResults.BadRequest(bodyObject);
            }

            case CachedResultKind.OkOfT:
            {
                T? bodyObject = GetBodyObject<T>(request);
                return TypedResults.Ok(bodyObject);
            }

            case CachedResultKind.CreatedAtRouteOfT:
            {
                T? bodyObject = GetBodyObject<T>(request);
                return TypedResults.CreatedAtRoute(bodyObject, request.ResultRouteName, request.ResultRouteValues);
            }

            case CachedResultKind.AcceptedAtRouteOfT:
            {
                T? bodyObject = GetBodyObject<T>(request);
                return TypedResults.AcceptedAtRoute(bodyObject, request.ResultRouteName, request.ResultRouteValues);
            }

            case CachedResultKind.BadRequestOfProblemDetails:
            {
                ProblemDetails? bodyObject = GetBodyObject<ProblemDetails>(request);
                return TypedResults.BadRequest(bodyObject);
            }

            case CachedResultKind.BadRequestOfString:
            {
                string? bodyObject = GetBodyObject<string>(request);
                return TypedResults.BadRequest(bodyObject);
            }

            default:
                throw new IdempotencyException($"Idempotency is not implemented for cached result kind '{request.ResultKind}'.");
        }
    }

    private async Task UpdateRequestWithResponseDataAsync(EndpointFilterInvocationContext ctx, object? executedContext, ApiRequest request, string cacheKey)
    {
        request.Headers = ctx.HttpContext.Response.Headers.ToDictionary(h => h.Key, h => h.Value.ToList());

        if (executedContext is null)
        {
            throw new IdempotencyException("Unknown result context type");
        }

        if (executedContext is IStatusCodeHttpResult scResult)
        {
            request.StatusCode = scResult.StatusCode;
        }

        switch (executedContext)
        {
            case Ok:
                request.ResultKind = CachedResultKind.Ok;
                break;

            case Created createdResult:
                request.ResultKind = CachedResultKind.Created;
                request.Location = createdResult.Location;
                break;

            case Accepted acceptedResult:
                request.ResultKind = CachedResultKind.Accepted;
                request.Location = acceptedResult.Location;
                break;

            case NotFound:
                request.ResultKind = CachedResultKind.NotFound;
                break;

            case UnauthorizedHttpResult:
                request.ResultKind = CachedResultKind.Unauthorized;
                break;

            case UnprocessableEntity:
                request.ResultKind = CachedResultKind.UnprocessableEntity;
                break;

            case ProblemHttpResult:
                request.ResultKind = CachedResultKind.Problem;
                break;

            case ForbidHttpResult:
                request.ResultKind = CachedResultKind.Forbidden;
                break;

            case Conflict:
                request.ResultKind = CachedResultKind.Conflict;
                break;

            case NoContent:
                request.ResultKind = CachedResultKind.NoContent;
                break;

            case Ok<T> okOfT:
                request.ResultKind = CachedResultKind.OkOfT;
                SetBody(request, CachedBodyKind.TValue, okOfT.Value);
                break;

            case CreatedAtRoute<T> createdAtRouteOfT:
                request.ResultKind = CachedResultKind.CreatedAtRouteOfT;
                SetBody(request, CachedBodyKind.TValue, createdAtRouteOfT.Value);
                request.ResultRouteName = createdAtRouteOfT.RouteName;
                request.ResultRouteValues = createdAtRouteOfT.RouteValues?.ToDictionary(r => r.Key, r => r.Value?.ToString());
                break;

            case AcceptedAtRoute<T> acceptedAtRouteOfT:
                request.ResultKind = CachedResultKind.AcceptedAtRouteOfT;
                SetBody(request, CachedBodyKind.TValue, acceptedAtRouteOfT.Value);
                request.ResultRouteName = acceptedAtRouteOfT.RouteName;
                request.ResultRouteValues = acceptedAtRouteOfT.RouteValues?.ToDictionary(r => r.Key, r => r.Value?.ToString());
                break;

            case BadRequest<ProblemDetails> badRequestOfProblemDetails:
                request.ResultKind = CachedResultKind.BadRequestOfProblemDetails;
                SetBody(request, CachedBodyKind.ProblemDetailsValue, badRequestOfProblemDetails.Value);
                break;

            case BadRequest<string> badRequestOfString:
                request.ResultKind = CachedResultKind.BadRequestOfString;
                SetBody(request, CachedBodyKind.StringValue, badRequestOfString.Value);
                break;

            case StatusCodeHttpResult statusCodeResult:
                request.ResultKind = CachedResultKind.StatusCodeOnlyMinimalApi;
                request.StatusCode = statusCodeResult.StatusCode;
                break;

            default:
                throw new IdempotencyException($"Idempotency is not implemented for result type {executedContext.GetType()}");
        }

        request.Status = IdempotencyRequestStatus.Completed;

        bool requestUpdatedSuccessfully = await _idempotencyService.SetResponseInCacheAsync(cacheKey, request, ctx.HttpContext.RequestAborted);

        if (!requestUpdatedSuccessfully)
        {
            throw new IdempotencyException("Failed to update request response.");
        }
    }

    private void SetBody(ApiRequest request, CachedBodyKind bodyKind, object? value)
    {
        request.BodyKind = bodyKind;
        request.Body = JsonSerializer.SerializeToUtf8Bytes(value, _serializerOptions);
    }

    private string GetIdempotencyKeyHeaderValue(HttpContext httpContext)
    {
        return httpContext.Request.Headers
            .TryGetValue(_options.IdempotencyHeader, out StringValues idempotencyKeyValue)
                ? idempotencyKeyValue.ToString()
                : string.Empty;
    }

    private TResult? GetBodyObject<TResult>(ApiRequest request) where TResult : class
    {
        if (request.Body is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TResult>(request.Body, _serializerOptions);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error deserializing body object");
            throw new IdempotencyException($"Error deserializing cached body as {typeof(TResult)}", ex);
        }
    }
}
