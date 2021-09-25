using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Delobytes.AspNetCore.Idempotency
{
    /// <summary>
    /// Фильтр идемпотентности: не допускает запросов без ключа идемпотентности,
    /// сохраняет запрос и результат в кеш, чтобы вернуть тот же ответ в случае запроса-дубликата.
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
            IDistributedCache distributedCache,
            IOptions<MvcOptions> mvcOptions,
            JsonSerializerOptions serializerOptions)
        {
            _log = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options.Value;
            _distributedCache = distributedCache ?? throw new ArgumentNullException(nameof(distributedCache));
            _mvcOptions = mvcOptions.Value;
            _serializerOptions = serializerOptions ?? throw new ArgumentNullException(nameof(serializerOptions));

            //SerializerOptions = new JsonSerializerOptions
            //{
            //    DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            //    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            //    Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
            //    WriteIndented = false
            //};
            //SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        }

        private readonly ILogger<IdempotencyFilterAttribute> _log;
        private readonly IdempotencyControlOptions _options;
        private readonly IDistributedCache _distributedCache;
        private readonly MvcOptions _mvcOptions;

        private readonly JsonSerializerOptions _serializerOptions;

        /// <summary>
        /// Проверяет идемпотентность и возвращает результат запроса из кеша если он уже был выполнен.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="next"></param>
        /// <returns></returns>
        public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
        {
            if (_options.Enabled)
            {
                string idempotencyKey = GetIdempotencyKeyHeaderValue(context.HttpContext);
                
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
                    string key = $"{_options.CacheKeysPrefix}:{idempotencyKey}";

                    string method = context.HttpContext.Request.Method;
                    string path = context.HttpContext.Request.Path.HasValue ? context.HttpContext.Request.Path.Value : null;
                    string query = context.HttpContext.Request.QueryString.HasValue ? context.HttpContext.Request.QueryString.ToUriComponent() : null;

                    (bool requestCreated, ApiRequest request) = await GetOrCreateRequestAsync(idempotencyKey, method, path, query);

                    if (!requestCreated)
                    {
                        HandleRequestNotCreated(context, request, method, path, query);
                        return;
                    }

                    ResourceExecutedContext executedContext = await next.Invoke();

                    await UpdateRequestDataAsync(executedContext, request, key);
                }
            }
            else
            {
                await next.Invoke();
            }
        }

        private async Task<(bool created, ApiRequest request)> GetOrCreateRequestAsync(string idempotencyKey, string method, string path, string query)
        {
            string key = $"{_options.CacheKeysPrefix}:{idempotencyKey}";

            string cachedApiRequest;

            DateTime startGetDt = DateTime.UtcNow;

            try
            {
                cachedApiRequest = await _distributedCache.GetStringAsync(key);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error getting cached value for key {CacheKey}", key);
                return (false, null);
            }
            finally
            {
                DateTime stopGetDt = DateTime.UtcNow;
                TimeSpan processingTime = stopGetDt - startGetDt;
                _log.LogInformation("cache.request.idempotency.get.msec {CacheRequestIdempotencyGetMsec}", (int)processingTime.TotalMilliseconds);
            }

            if (cachedApiRequest is not null && cachedApiRequest.Length > 0)
            {
                ApiRequest requestFromCache = JsonSerializer.Deserialize<ApiRequest>(cachedApiRequest, _serializerOptions);

                return (false, requestFromCache);
            }
            else
            {
                ApiRequest apiRequest = new ApiRequest();
                apiRequest.ApiRequestID = idempotencyKey;
                apiRequest.Method = method;
                apiRequest.Path = path;
                apiRequest.Query = query;

                string serializedRequest = JsonSerializer.Serialize(apiRequest, _serializerOptions);

                DateTime startSetDt = DateTime.UtcNow;

                try
                {
                    await _distributedCache.SetStringAsync(key, serializedRequest, new DistributedCacheEntryOptions { AbsoluteExpiration = DateTimeOffset.UtcNow.AddDays(1) });
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Error adding cached value for key {CacheKey}", key);
                    return (false, null);
                }
                finally
                {
                    DateTime stopSetDt = DateTime.UtcNow;
                    TimeSpan processingTime = stopSetDt - startSetDt;
                    _log.LogInformation("cache.request.idempotency.create.msec {CacheRequestIdempotencyCreateMsec}", (int)processingTime.TotalMilliseconds);
                }

                return (true, apiRequest);
            }
        }

        private void HandleRequestNotCreated(ResourceExecutingContext context, ApiRequest request, string method, string path, string query)
        {
            if (request is null)
            {
                throw new IdempotencyException("Failed to create request.");
            }

            if (method != request.Method || path != request.Path || query != request.Query)
            {
                context.Result = new ConflictObjectResult("В кеше исполнения уже есть запрос с таким идентификатором и его параметры отличны от текущего запроса.");
                return;
            }

            context.HttpContext.Response.StatusCode = request.StatusCode ?? 0;

            string outputMediaType = string.Empty;

            foreach (KeyValuePair<string, List<string>> item in request.Headers)
            {
                string headerValue = string.Join(";", item.Value);

                context.HttpContext.Response.Headers[item.Key] = headerValue;

                if (string.Equals(item.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    outputMediaType = headerValue;
                }
            }

            Type contextResultType = Type.GetType(request.ResultType);

            if (contextResultType == typeof(CreatedAtRouteResult))
            {
                Type bodyType = Type.GetType(request.BodyType);
                object bodyObject = JsonSerializer.Deserialize(request.Body, bodyType, _serializerOptions);

                CreatedAtRouteResult result = new CreatedAtRouteResult(request.ResultRouteName, request.ResultRouteValues, bodyObject);
                result.DeclaredType = bodyType;
                result.StatusCode = request.StatusCode;

                OutputFormatter formatter = GetOutputFormatter(outputMediaType, _options.BodyOutputFormatterType);
                result.Formatters.Add(formatter);

                context.Result = result;
            }
            else if (contextResultType.BaseType == typeof(ObjectResult))
            {
                Type bodyType = Type.GetType(request.BodyType);
                object bodyObject = JsonSerializer.Deserialize(request.Body, bodyType, _serializerOptions);

                ObjectResult result = new ObjectResult(bodyObject)
                {
                    StatusCode = request.StatusCode,
                    DeclaredType = bodyType
                };

                OutputFormatter formatter = GetOutputFormatter(outputMediaType, _options.BodyOutputFormatterType);
                result.Formatters.Add(formatter);

                context.Result = result;
            }
            else if (contextResultType.BaseType == typeof(StatusCodeResult)
                || contextResultType.BaseType == typeof(ActionResult))
            {
                context.Result = new StatusCodeResult(request.StatusCode ?? 0);
            }
            else
            {
                throw new IdempotencyException($"Idempotency is not implemented for IActionResult type {contextResultType}");
            }

            _log.LogInformation("Cached response returned from IdempotencyFilter.");
        }

        private async Task UpdateRequestDataAsync(ResourceExecutedContext executedContext, ApiRequest request, string cacheKey)
        {
            request.StatusCode = executedContext.HttpContext.Response.StatusCode;
            request.Headers = executedContext
                .HttpContext.Response.Headers.ToDictionary(h => h.Key, h => h.Value.ToList());

            if (executedContext.Result != null)
            {
                request.ResultType = executedContext.Result.GetType().AssemblyQualifiedName;

                switch (executedContext.Result)
                {
                    case CreatedAtRouteResult createdRequestResult:
                    {
                        SetBody(request, createdRequestResult);

                        request.ResultRouteName = createdRequestResult.RouteName;

                        Dictionary<string, string> routeValues = createdRequestResult
                            .RouteValues.ToDictionary(r => r.Key, r => r.Value.ToString());
                        request.ResultRouteValues = routeValues;

                        break;
                    }
                    case ObjectResult objectRequestResult:
                    {
                        SetBody(request, objectRequestResult);

                        break;
                    }
                    case NoContentResult noContentResult:
                    case OkResult okResult:
                    case StatusCodeResult statusCodeResult:
                    case ActionResult actionResult:
                    {
                        // известные типы, которым не нужны дополнительные данные
                        break;
                    }
                    default:
                    {
                        throw new IdempotencyException($"Idempotency is not implemented for result type {executedContext.GetType()}");
                    }
                }
            }

            bool requestUpdatedSuccessfully = await SetResponseInCacheAsync(cacheKey, request);

            if (!requestUpdatedSuccessfully)
            {
                throw new IdempotencyException("Failed to set request response.");
            }
        }

        private async Task<bool> SetResponseInCacheAsync(string key, ApiRequest apiRequest)
        {
            string serializedRequest = JsonSerializer.Serialize(apiRequest, _serializerOptions);

            DateTime startSetDt = DateTime.UtcNow;

            try
            {
                await _distributedCache.SetStringAsync(key, serializedRequest, new DistributedCacheEntryOptions { AbsoluteExpiration = DateTimeOffset.UtcNow.AddDays(1) });
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error updating cached value for key {CacheKey}", key);
                return false;
            }
            finally
            {
                DateTime stopSetDt = DateTime.UtcNow;
                TimeSpan processingTime = stopSetDt - startSetDt;
                _log.LogInformation("cache.request.idempotency.update.msec {CacheRequestIdempotencyUpdateMsec}", (int)processingTime.TotalMilliseconds);
            }

            return true;
        }

        private string GetIdempotencyKeyHeaderValue(HttpContext httpContext)
        {
            if (httpContext.Request.Headers
                .TryGetValue(_options.IdempotencyHeader, out StringValues idempotencyKeyValue))
            {
                return idempotencyKeyValue.ToString();
            }
            else
            {
                return string.Empty;
            }
        }

        private void SetBody(ApiRequest request, ObjectResult objectRequestResult)
        {
            string bodyType = objectRequestResult.Value.GetType().AssemblyQualifiedName;
            request.BodyType = bodyType;
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(objectRequestResult.Value, _serializerOptions);
            request.Body = body;
        }

        private OutputFormatter GetOutputFormatter(string mediaType, OutputFormatterType formatterType)
        {
            if (_mvcOptions.OutputFormatters.Count == 0)
            {
                return CreateJsonFormatter(mediaType, formatterType);
            }

            OutputFormatter properFormatter = null;

            foreach (IOutputFormatter formatter in _mvcOptions.OutputFormatters)
            {
                OutputFormatter outputFormatter = formatter as OutputFormatter;

                if (outputFormatter is not null)
                {
                    bool jsonUtf8Formatter = outputFormatter.SupportedMediaTypes.Any(e => e == mediaType);

                    if (jsonUtf8Formatter)
                    {
                        properFormatter = outputFormatter;
                        break;
                    }
                }
            }

            return (properFormatter is not null) ? properFormatter : CreateJsonFormatter(mediaType, formatterType);
        }

        private static OutputFormatter CreateJsonFormatter(string mediaType, OutputFormatterType formatterType)
        {
            OutputFormatter result = null;

            switch (formatterType)
            {
                case OutputFormatterType.Newtonsoft:
                    NewtonsoftJsonOutputFormatter newtonsoftFormatter = GetNewtonsoftJsonOutputFormatter();

                    if (!newtonsoftFormatter.SupportedMediaTypes.Any(e => e == mediaType))
                    {
                        newtonsoftFormatter.SupportedMediaTypes.Insert(0, mediaType);
                    }

                    result = newtonsoftFormatter;
                    break;

                case OutputFormatterType.SystemText:
                    SystemTextJsonOutputFormatter systemtextFormatter = GetSystemTextJsonOutputFormatter();

                    if (!systemtextFormatter.SupportedMediaTypes.Any(e => e == mediaType))
                    {
                        systemtextFormatter.SupportedMediaTypes.Insert(0, mediaType);
                    }

                    result = systemtextFormatter;
                    break;

                default:
                    throw new NotImplementedException($"Body output formatter for type '{formatterType}' is not implemented.");
            }

            return result;
        }

        private static SystemTextJsonOutputFormatter GetSystemTextJsonOutputFormatter()
        {
            IServiceCollection services = new ServiceCollection()
                .AddLogging()
                .AddMvc()
                .Services;
            ServiceProvider serviceProvider = services.BuildServiceProvider();
            MvcOptions mvcOptions = serviceProvider.GetRequiredService<IOptions<MvcOptions>>().Value;
            return mvcOptions.OutputFormatters
                .OfType<SystemTextJsonOutputFormatter>()
                .Last();
        }

        private static NewtonsoftJsonOutputFormatter GetNewtonsoftJsonOutputFormatter()
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
            ServiceProvider serviceProvider = services.BuildServiceProvider();
            MvcOptions mvcOptions = serviceProvider.GetRequiredService<IOptions<MvcOptions>>().Value;
            return mvcOptions.OutputFormatters
                .OfType<NewtonsoftJsonOutputFormatter>()
                .Last();
        }
    }
}
