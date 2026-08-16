using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Delobytes.AspNetCore.Idempotency;

/// <summary>
/// Фильтр идемпотентности: сохраняет результаты запросов с ключом идемпотентности в кэш,
/// чтобы вернуть тот же ответ в случае запроса-дубликата.
/// Реализация по примеру https://stripe.com/docs/api/idempotent_requests
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public class IdempotencyFilterAttribute : Attribute, IAsyncResourceFilter
{
    /// <summary>
    /// Конструктор.
    /// </summary>
    public IdempotencyFilterAttribute(ILogger<IdempotencyFilterAttribute> logger,
        IOptions<IdempotencyControlOptions> options,
        RequestCachingService idempotencyService,
        IOptions<MvcOptions> mvcOptions,
        JsonSerializerOptions serializerOptions)
    {
        _log = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value;
        _idempotencyService = idempotencyService ?? throw new ArgumentNullException(nameof(idempotencyService));
        _mvcOptions = mvcOptions.Value;
        _serializerOptions = serializerOptions ?? throw new ArgumentNullException(nameof(serializerOptions));
    }

    private readonly ILogger<IdempotencyFilterAttribute> _log;
    private readonly IdempotencyControlOptions _options;
    private readonly RequestCachingService _idempotencyService;
    private readonly MvcOptions _mvcOptions;
    private readonly JsonSerializerOptions _serializerOptions;

    private static readonly Lazy<SystemTextJsonOutputFormatter> _systemTextJsonFormatter =
        new Lazy<SystemTextJsonOutputFormatter>(InitializeSystemTextJsonOutputFormatter);

    private static readonly Lazy<NewtonsoftJsonOutputFormatter> _newtonsoftJsonFormatter =
        new Lazy<NewtonsoftJsonOutputFormatter>(InitializeNewtonsoftJsonOutputFormatter);

    /// <summary>
    /// Проверяет идемпотентность и возвращает результат запроса из кэша если он уже был выполнен.
    /// </summary>
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        if (!_options.Enabled)
        {
            await next.Invoke();
            return;
        }

        string idempotencyKey = context.HttpContext.Request.Headers
            .TryGetValue(_options.IdempotencyHeader, out StringValues idempotencyKeyValue) ? idempotencyKeyValue.ToString() : string.Empty;

        if (string.IsNullOrEmpty(idempotencyKey))
        {
            if (_options.HeaderRequired)
            {
                context.Result = new BadRequestObjectResult($"Запрос не содержит заголовка {_options.IdempotencyHeader} или значение в нём неверно.");
                return;
            }
            else
            {
                await next.Invoke();
            }
        }
        else
        {
            string cacheKey = $"{_options.CacheKeysPrefix}:{idempotencyKey}";

            string method = context.HttpContext.Request.Method;
            string? path = context.HttpContext.Request.Path.HasValue ? context.HttpContext.Request.Path.Value : null;
            string? query = context.HttpContext.Request.QueryString.HasValue ? context.HttpContext.Request.QueryString.ToUriComponent() : null;

            (bool requestCreated, ApiRequest? request) = await _idempotencyService.GetOrCreateRequestAsync(context.HttpContext, idempotencyKey, cacheKey, method, path, query);

            if (!requestCreated)
            {
                if (request != null)
                {
                    switch (request.Status)
                    {
                        case IdempotencyRequestStatus.InProgress:
                            context.HttpContext.Response.StatusCode = 425;
                            context.Result = new StatusCodeResult(425);
                            return;

                        case IdempotencyRequestStatus.Completed:
                            if (request.ResultKind == CachedResultKind.Unknown)
                            {
                                _log.LogInformation("There is no cached response data for the request");
                                context.HttpContext.Response.StatusCode = 500;
                                context.Result = new StatusCodeResult(500);
                                return;
                            }

                            if (method != request.Method || path != request.Path || query != request.Query)
                            {
                                _log.LogInformation("Idempotency cache already contains {ApiRequestID} and its properties are different from the current request", request.ApiRequestID);
                                context.HttpContext.Response.StatusCode = 409;
                                context.Result = new StatusCodeResult(409);
                                return;
                            }

                            UpdateContextWithCachedResult(context, request, method, path, query);
                            return;

                        case IdempotencyRequestStatus.Unknown:
                            context.HttpContext.Response.StatusCode = 500;
                            context.Result = new StatusCodeResult(500);
                            return;

                        default:
                            throw new IdempotencyException($"Unexpected request status: {request.Status}");
                    }
                }
                else
                {
                    if (!_options.Optional)
                    {
                        throw new IdempotencyException("Error getting cached request");
                    }
                }
            }

            ResourceExecutedContext executedContext = await next.Invoke();

            if (requestCreated && request != null)
            {
                await UpdateRequestWithResponseDataAsync(executedContext, request, cacheKey);
            }
        }
    }

    private void UpdateContextWithCachedResult(ResourceExecutingContext context, ApiRequest request, string method, string? path, string? query)
    {
        if (method != request.Method || path != request.Path || query != request.Query)
        {
            _log.LogInformation("Idempotency cache already contains {ApiRequestID} and its properties are different from the current request", request.ApiRequestID);
            context.Result = new ConflictObjectResult($"В кеше исполнения уже есть запрос с идентификатором {request.ApiRequestID} и его параметры отличаются от текущего запроса.");
            return;
        }

        context.HttpContext.Response.StatusCode = request.StatusCode ?? 0;

        if (request.Headers == null)
        {
            throw new IdempotencyException("Response headers is not found.");
        }

        string outputMediaType = string.Empty;

        foreach (KeyValuePair<string, List<string?>> item in request.Headers)
        {
            string headerValue = string.Join(";", item.Value);
            context.HttpContext.Response.Headers[item.Key] = headerValue;

            if (string.Equals(item.Key, HeaderNames.ContentType, StringComparison.OrdinalIgnoreCase))
            {
                outputMediaType = headerValue;
            }
        }

        switch (request.ResultKind)
        {
            case CachedResultKind.MvcCreatedAtRouteResult:
            {
                if (outputMediaType == string.Empty)
                {
                    throw new IdempotencyException("Output media type type is not found.");
                }

                (object? bodyObject, Type bodyType) = GetBodyObject(request);

                CreatedAtRouteResult result = new CreatedAtRouteResult(request.ResultRouteName, request.ResultRouteValues, bodyObject);
                result.DeclaredType = bodyType;
                result.StatusCode = request.StatusCode;

                OutputFormatter formatter = GetOutputFormatter(outputMediaType, _options.BodyOutputFormatterType);
                result.Formatters.Add(formatter);

                context.Result = result;
                break;
            }
            case CachedResultKind.MvcObjectResult:
            {
                if (outputMediaType == string.Empty)
                {
                    throw new IdempotencyException("Output media type type is not found.");
                }

                (object? bodyObject, Type bodyType) = GetBodyObject(request);

                ObjectResult result = new ObjectResult(bodyObject)
                {
                    StatusCode = request.StatusCode,
                    DeclaredType = bodyType
                };

                OutputFormatter formatter = GetOutputFormatter(outputMediaType, _options.BodyOutputFormatterType);
                result.Formatters.Add(formatter);

                context.Result = result;
                break;
            }
            case CachedResultKind.MvcStatusCodeOnly:
            {
                context.Result = new StatusCodeResult(request.StatusCode ?? 0);
                break;
            }
            default:
            {
                throw new IdempotencyException($"Idempotency is not implemented for cached result kind '{request.ResultKind}'.");
            }
        }

        _log.LogInformation("Cached response returned from IdempotencyFilter.");
    }

    private async Task UpdateRequestWithResponseDataAsync(ResourceExecutedContext executedContext, ApiRequest request, string cacheKey)
    {
        request.StatusCode = executedContext.HttpContext.Response.StatusCode;
        request.Headers = executedContext
            .HttpContext.Response.Headers.ToDictionary(h => h.Key, h => h.Value.ToList());

        if (executedContext.Result != null)
        {
            switch (executedContext.Result)
            {
                case CreatedAtRouteResult createdRequestResult:
                {
                    request.ResultKind = CachedResultKind.MvcCreatedAtRouteResult;

                    SetBody(request, createdRequestResult.Value);

                    request.ResultRouteName = createdRequestResult.RouteName;

                    Dictionary<string, string?>? routeValues = createdRequestResult
                        .RouteValues?.ToDictionary(r => r.Key, r => r.Value?.ToString());
                    request.ResultRouteValues = routeValues;

                    break;
                }
                case ObjectResult objectRequestResult:
                {
                    request.ResultKind = CachedResultKind.MvcObjectResult;

                    SetBody(request, objectRequestResult.Value);

                    break;
                }
                case NoContentResult noContentResult:
                case OkResult okResult:
                case StatusCodeResult statusCodeResult:
                case ActionResult actionResult:
                {
                    request.ResultKind = CachedResultKind.MvcStatusCodeOnly;
                    // известные типы, которым не нужны дополнительные данные
                    break;
                }
                default:
                {
                    throw new IdempotencyException($"Idempotency is not implemented for result type {executedContext.Result.GetType()}");
                }
            }
        }

        request.Status = IdempotencyRequestStatus.Completed;

        bool requestUpdatedSuccessfully = await _idempotencyService.SetResponseInCacheAsync(cacheKey, request, executedContext.HttpContext.RequestAborted);

        if (!requestUpdatedSuccessfully)
        {
            throw new IdempotencyException("Failed to set request response.");
        }
    }

    private void SetBody(ApiRequest request, object? value)
    {
        if (value is not null)
        {
            request.BodyTypeKey = IdempotencyBodyTypeRegistry.GetKey(value.GetType());
        }

        request.Body = JsonSerializer.SerializeToUtf8Bytes(value, _serializerOptions);
    }

    private OutputFormatter GetOutputFormatter(string mediaType, OutputFormatterType formatterType)
    {
        if (_mvcOptions.OutputFormatters.Count == 0)
        {
            return CreateJsonFormatter(mediaType, formatterType);
        }

        OutputFormatter? properFormatter = null;

        foreach (IOutputFormatter formatter in _mvcOptions.OutputFormatters)
        {
            OutputFormatter? outputFormatter = formatter as OutputFormatter;

            if (outputFormatter is not null && outputFormatter.SupportedMediaTypes.Any(e => e == mediaType))
            {
                properFormatter = outputFormatter;
                break;
            }
        }

        return (properFormatter is not null) ? properFormatter : CreateJsonFormatter(mediaType, formatterType);
    }

    private static OutputFormatter CreateJsonFormatter(string mediaType, OutputFormatterType formatterType)
    {
        switch (formatterType)
        {
            case OutputFormatterType.Newtonsoft:
                NewtonsoftJsonOutputFormatter newtonsoftFormatter = _newtonsoftJsonFormatter.Value;

                if (!newtonsoftFormatter.SupportedMediaTypes.Any(e => e == mediaType))
                {
                    newtonsoftFormatter.SupportedMediaTypes.Insert(0, mediaType);
                }

                return newtonsoftFormatter;

            case OutputFormatterType.SystemText:
                SystemTextJsonOutputFormatter systemtextFormatter = _systemTextJsonFormatter.Value;

                if (!systemtextFormatter.SupportedMediaTypes.Any(e => e == mediaType))
                {
                    systemtextFormatter.SupportedMediaTypes.Insert(0, mediaType);
                }

                return systemtextFormatter;

            default:
                throw new NotImplementedException($"Body output formatter for type '{formatterType}' is not implemented.");
        }
    }

    private static SystemTextJsonOutputFormatter InitializeSystemTextJsonOutputFormatter()
    {
        IServiceCollection services = new ServiceCollection()
            .AddLogging()
            .AddMvc()
            .Services;

        using (ServiceProvider serviceProvider = services.BuildServiceProvider())
        {
            MvcOptions mvcOptions = serviceProvider.GetRequiredService<IOptions<MvcOptions>>().Value;
            return mvcOptions.OutputFormatters
                .OfType<SystemTextJsonOutputFormatter>()
                .Last();
        }
    }

    private static NewtonsoftJsonOutputFormatter InitializeNewtonsoftJsonOutputFormatter()
    {
        IServiceCollection services = new ServiceCollection()
            .AddLogging()
            .AddMvc()
            .AddNewtonsoftJson(options =>
            {
                options.SerializerSettings.Converters.Add(new Newtonsoft.Json.Converters.StringEnumConverter());
                options.SerializerSettings.DateParseHandling = Newtonsoft.Json.DateParseHandling.DateTimeOffset;
            })
            .Services;

        using (ServiceProvider serviceProvider = services.BuildServiceProvider())
        {
            MvcOptions mvcOptions = serviceProvider.GetRequiredService<IOptions<MvcOptions>>().Value;
            return mvcOptions.OutputFormatters
                .OfType<NewtonsoftJsonOutputFormatter>()
                .Last();
        }
    }

    private (object? bodyObject, Type bodyType) GetBodyObject(ApiRequest request)
    {
        if (request.Body is null)
        {
            return (null, typeof(object));
        }

        if (!_options.BodyTypeRegistry.TryResolve(request.BodyTypeKey, out Type? bodyType) || bodyType is null)
        {
            throw new IdempotencyException(
                $"Type '{request.BodyTypeKey}' is not registered in IdempotencyControlOptions.BodyTypeRegistry. " +
                "Register it explicitly at startup via options.BodyTypeRegistry.Add<T>(), or configure " +
                "options.BodyTypeRegistry.SetResolver(...) to resolve it dynamically, to allow replaying " +
                "cached responses of this type.");
        }

        object? bodyObject = JsonSerializer.Deserialize(request.Body, bodyType, _serializerOptions);

        return (bodyObject, bodyType);
    }
}
