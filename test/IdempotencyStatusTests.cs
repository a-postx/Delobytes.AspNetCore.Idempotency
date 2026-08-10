using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Delobytes.AspNetCore.Idempotency.Tests;

public class IdempotencyStatusTests
{
    private static readonly string RequestMethod   = "POST";
    private static readonly string RequestPath     = "/json";
    private static readonly string RequestQueryString = "?pageSize=5";
    private static readonly string IdempotencyHeader  = "Idempotency-Key";
    private static readonly string IdempotencyKey     = "c903b5ac-ce6d-47d5-aac0-1ddad0f308c9";
    private static readonly string CacheKeysPrefix    = "idempotency_keys";

    private static readonly Dictionary<string, StringValues> RequestHeadersWithKey =
        new() { { "RequestHeader1", "v1" }, { "Idempotency-Key", IdempotencyKey } };

    private static readonly Dictionary<string, StringValues> ResponseHeaders =
        new() { { "ResponseHeader1", "rv1" }, { "Content-Type", "application/json" } };

    private static readonly Action<IdempotencyControlOptions> RegularOptions = o =>
    {
        o.Enabled        = true;
        o.Optional       = false;
        o.HeaderRequired = true;
        o.IdempotencyHeader = IdempotencyHeader;
    };

    private static readonly EndpointFilterDelegate DelegateOkObject =
        new EndpointFilterDelegate(async _ => { await Task.Delay(10); return TypedResults.Ok(new TestObj { Id = 99 }); });

    private static readonly EndpointFilterDelegate DelegateBadRequest =
        new EndpointFilterDelegate(async _ => { await Task.Delay(10); return TypedResults.BadRequest("should-not-run"); });

    private sealed class TestObj { public int Id { get; set; } }

    private WebApplication BuildApp()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddIdempotencyControl(RegularOptions);
        builder.Services.AddScoped<IdempotencyFilterAttribute>();
        builder.Services.AddScoped<IdempotencyEndpointFilter<TestObj>>();
        return builder.Build();
    }

    private (ResourceExecutingContext ctx, ResourceExecutionDelegate next) MvcContexts(
        HttpContext pre, HttpContext post, IActionResult result)
    {
        ActionContext preAc  = new ActionContext(pre,  new RouteData(), new ActionDescriptor());
        ActionContext postAc = new ActionContext(post, new RouteData(), new ActionDescriptor());
        List<IFilterMetadata> filters = new List<IFilterMetadata>();
        ResourceExecutedContext executed = new ResourceExecutedContext(postAc, filters) { Result = result };
        ResourceExecutingContext executing = new ResourceExecutingContext(preAc, filters, new List<IValueProviderFactory>());
        return (executing, new ResourceExecutionDelegate(() => Task.FromResult(executed)));
    }

    private HttpContext MakeHttpContext(Dictionary<string, StringValues> reqHeaders,
        Dictionary<string, StringValues>? respHeaders = null, int statusCode = 200)
    {
        FeatureCollection features = new FeatureCollection();

        Mock<IHttpRequestFeature> reqMock = new Mock<IHttpRequestFeature>();
        reqMock.Setup(r => r.Method).Returns(RequestMethod);
        reqMock.Setup(r => r.Path).Returns(RequestPath);
        reqMock.Setup(r => r.QueryString).Returns(RequestQueryString);
        reqMock.Setup(r => r.Headers).Returns(new HeaderDictionary(reqHeaders));
        reqMock.Setup(r => r.Body).Returns(new System.IO.MemoryStream());
        features.Set(reqMock.Object);

        Mock<IHttpResponseFeature> respMock = new Mock<IHttpResponseFeature>();
        respMock.SetupProperty(r => r.StatusCode);

        if (respHeaders != null)
        {
            respMock.Setup(r => r.Headers).Returns(new HeaderDictionary(respHeaders));
        }

        features.Set(respMock.Object);

        DefaultHttpContext ctx = new DefaultHttpContext(features);
        ctx.Response.StatusCode = statusCode;
        return ctx;
    }


    [Fact]
    public void Status_Unknown_HasIntValueZero()
    {
        ((int)IdempotencyRequestStatus.Unknown).Should().Be(0);
    }

    [Fact]
    public void Status_InProgress_HasIntValueOne()
    {
        ((int)IdempotencyRequestStatus.InProgress).Should().Be(1);
    }

    [Fact]
    public void Status_Completed_HasIntValueTwo()
    {
        ((int)IdempotencyRequestStatus.Completed).Should().Be(2);
    }

    [Fact]
    public void ApiRequest_DefaultStatus_IsUnknown()
    {
        ApiRequest req = new ApiRequest("id", "POST");
        req.Status.Should().Be(IdempotencyRequestStatus.Unknown);
    }

    [Fact]
    public void ApiRequest_StatusRoundTrip_InProgress()
    {
        ApiRequest req = new ApiRequest("id", "POST") { Status = IdempotencyRequestStatus.InProgress };
        JsonSerializerOptions opts = new JsonSerializerOptions();
        string json = JsonSerializer.Serialize(req, opts);
        ApiRequest restored = JsonSerializer.Deserialize<ApiRequest>(json, opts)!;
        restored.Status.Should().Be(IdempotencyRequestStatus.InProgress);
    }

    [Fact]
    public void ApiRequest_StatusRoundTrip_Completed()
    {
        ApiRequest req = new ApiRequest("id", "POST") { Status = IdempotencyRequestStatus.Completed };
        JsonSerializerOptions opts = new JsonSerializerOptions();
        string json = JsonSerializer.Serialize(req, opts);
        ApiRequest restored = JsonSerializer.Deserialize<ApiRequest>(json, opts)!;
        restored.Status.Should().Be(IdempotencyRequestStatus.Completed);
    }

    [Fact]
    public void ApiRequest_StatusTransition_UnknownToInProgressToCompleted()
    {
        ApiRequest req = new ApiRequest("id", "POST");
        req.Status.Should().Be(IdempotencyRequestStatus.Unknown);

        req.Status = IdempotencyRequestStatus.InProgress;
        req.Status.Should().Be(IdempotencyRequestStatus.InProgress);

        req.Status = IdempotencyRequestStatus.Completed;
        req.Status.Should().Be(IdempotencyRequestStatus.Completed);
    }


    [Fact]
   public async Task Attribute_FirstRequest_WritesInProgressThenCompleted()
    {
        WebApplication app = BuildApp();
        IdempotencyFilterAttribute attr = app.Services.GetRequiredService<IdempotencyFilterAttribute>();
        IDistributedCache cache = app.Services.GetRequiredService<IDistributedCache>();
        JsonSerializerOptions jOpts = app.Services.GetRequiredService<JsonSerializerOptions>();

        HttpContext pre = MakeHttpContext(RequestHeadersWithKey);
        HttpContext post = MakeHttpContext(RequestHeadersWithKey, ResponseHeaders, 200);
        (ResourceExecutingContext? ctx, ResourceExecutionDelegate? next) = MvcContexts(pre, post, new OkResult());

        string cacheKey = $"{CacheKeysPrefix}:{IdempotencyKey}";

        
        await attr.OnResourceExecutionAsync(ctx, next);


        string? raw = await cache.GetStringAsync(cacheKey);
        raw.Should().NotBeNullOrEmpty();

        ApiRequest cached = JsonSerializer.Deserialize<ApiRequest>(raw!, jOpts)!;
        cached.Status.Should().Be(IdempotencyRequestStatus.Completed);
    }

    [Fact]
    public async Task Attribute_SecondRequestWithInProgressKey_Returns425()
    {
        WebApplication app = BuildApp();
        IdempotencyFilterAttribute attr = app.Services.GetRequiredService<IdempotencyFilterAttribute>();
        IDistributedCache cache = app.Services.GetRequiredService<IDistributedCache>();
        JsonSerializerOptions jOpts = app.Services.GetRequiredService<JsonSerializerOptions>();

        ApiRequest inProgressEntry = new ApiRequest(IdempotencyKey, RequestMethod)
        {
            Path = RequestPath,
            Query = RequestQueryString,
            Status = IdempotencyRequestStatus.InProgress
        };
        string cacheKey = $"{CacheKeysPrefix}:{IdempotencyKey}";
        await cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(inProgressEntry, jOpts));

        HttpContext pre = MakeHttpContext(RequestHeadersWithKey);
        HttpContext post = MakeHttpContext(RequestHeadersWithKey, ResponseHeaders);
        (ResourceExecutingContext? ctx, ResourceExecutionDelegate? next) = MvcContexts(pre, post, new OkObjectResult("OK"));

        
        await attr.OnResourceExecutionAsync(ctx, next);


        ctx.Result.Should().NotBeNull();
        ctx.Result.Should().BeOfType<StatusCodeResult>();
        ((StatusCodeResult)ctx.Result!).StatusCode.Should().Be(425);
    }

    [Fact]
    public async Task Attribute_SecondRequestWithCompletedKey_ReturnsCachedResult()
    {
        WebApplication app = BuildApp();
        IdempotencyFilterAttribute attr = app.Services.GetRequiredService<IdempotencyFilterAttribute>();
        IDistributedCache cache = app.Services.GetRequiredService<IDistributedCache>();
        JsonSerializerOptions jOpts = app.Services.GetRequiredService<JsonSerializerOptions>();

        HttpContext pre = MakeHttpContext(RequestHeadersWithKey);
        HttpContext post = MakeHttpContext(RequestHeadersWithKey, ResponseHeaders, 202);
        (ResourceExecutingContext? ctx, ResourceExecutionDelegate? next) = MvcContexts(pre, post, new OkResult());
        await attr.OnResourceExecutionAsync(ctx, next);

        
        string cacheKey = $"{CacheKeysPrefix}:{IdempotencyKey}";
        string? raw = await cache.GetStringAsync(cacheKey);
        ApiRequest cached = JsonSerializer.Deserialize<ApiRequest>(raw!, jOpts)!;
        cached.Status.Should().Be(IdempotencyRequestStatus.Completed);


        IdempotencyFilterAttribute attr2 = app.Services.GetRequiredService<IdempotencyFilterAttribute>();
        HttpContext pre2 = MakeHttpContext(RequestHeadersWithKey, ResponseHeaders);
        HttpContext post2 = MakeHttpContext(RequestHeadersWithKey, ResponseHeaders);
        (ResourceExecutingContext? ctx2, ResourceExecutionDelegate? next2) = MvcContexts(pre2, post2, new BadRequestResult());
        await attr2.OnResourceExecutionAsync(ctx2, next2);


        ctx2.Result.Should().NotBeNull();
        ctx2.Result.Should().BeOfType<StatusCodeResult>();
        ((StatusCodeResult)ctx2.Result!).StatusCode.Should().Be(202);
    }

    [Fact]
    public async Task Attribute_StatusTransition_InProgressBeforeHandlerCompleted()
    {
        WebApplication app = BuildApp();
        IdempotencyFilterAttribute attr = app.Services.GetRequiredService<IdempotencyFilterAttribute>();
        IDistributedCache cache = app.Services.GetRequiredService<IDistributedCache>();
        JsonSerializerOptions jOpts = app.Services.GetRequiredService<JsonSerializerOptions>();

        string cacheKey = $"{CacheKeysPrefix}:{IdempotencyKey}";
        ApiRequest? request = null;

        HttpContext pre = MakeHttpContext(RequestHeadersWithKey);
        HttpContext postCtx = MakeHttpContext(RequestHeadersWithKey, ResponseHeaders, 200);
        ActionContext postAc = new ActionContext(postCtx, new RouteData(), new ActionDescriptor());
        List<IFilterMetadata> filters = new List<IFilterMetadata>();
        ResourceExecutedContext executed = new ResourceExecutedContext(postAc, filters) { Result = new OkResult() };

        ResourceExecutionDelegate slowNext = new ResourceExecutionDelegate(async () =>
        {
            string? raw = await cache.GetStringAsync(cacheKey);

            if (raw != null)
            {
                request = JsonSerializer.Deserialize<ApiRequest>(raw, jOpts);
            }
                
            return await Task.FromResult(executed);
        });

        ActionContext preAc = new ActionContext(pre, new RouteData(), new ActionDescriptor());
        ResourceExecutingContext executing = new ResourceExecutingContext(preAc, filters, new List<IValueProviderFactory>());


        await attr.OnResourceExecutionAsync(executing, slowNext);


        request.Should().NotBeNull();
        request!.Status.Should().Be(IdempotencyRequestStatus.InProgress);
    }


    [Fact]
    public async Task EndpointFilter_FirstRequest_WritesInProgressThenCompleted()
    {
        WebApplication app = BuildApp();
        IdempotencyEndpointFilter<TestObj> filter = app.Services.GetRequiredService<IdempotencyEndpointFilter<TestObj>>();
        IDistributedCache cache = app.Services.GetRequiredService<IDistributedCache>();
        JsonSerializerOptions jOpts = app.Services.GetRequiredService<JsonSerializerOptions>();

        HttpContext httpCtx = MakeHttpContext(RequestHeadersWithKey, ResponseHeaders);
        DefaultEndpointFilterInvocationContext ctx = new DefaultEndpointFilterInvocationContext(httpCtx);
        string cacheKey = $"{CacheKeysPrefix}:{IdempotencyKey}";

       
        await filter.InvokeAsync(ctx, DelegateOkObject);


        string? raw = await cache.GetStringAsync(cacheKey);
        raw.Should().NotBeNullOrEmpty();
        ApiRequest cached = JsonSerializer.Deserialize<ApiRequest>(raw!, jOpts)!;
        cached.Status.Should().Be(IdempotencyRequestStatus.Completed);
    }

    [Fact]
    public async Task EndpointFilter_SecondRequestWithInProgressKey_Returns425()
    {
        WebApplication app = BuildApp();
        IdempotencyEndpointFilter<TestObj> filter = app.Services.GetRequiredService<IdempotencyEndpointFilter<TestObj>>();
        IDistributedCache cache = app.Services.GetRequiredService<IDistributedCache>();
        JsonSerializerOptions jOpts = app.Services.GetRequiredService<JsonSerializerOptions>();

        ApiRequest inProgressEntry = new ApiRequest(IdempotencyKey, RequestMethod)
        {
            Path = RequestPath,
            Query = RequestQueryString,
            Status = IdempotencyRequestStatus.InProgress
        };
        string cacheKey = $"{CacheKeysPrefix}:{IdempotencyKey}";
        await cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(inProgressEntry, jOpts));

        HttpContext httpCtx = MakeHttpContext(RequestHeadersWithKey, ResponseHeaders);
        DefaultEndpointFilterInvocationContext ctx = new DefaultEndpointFilterInvocationContext(httpCtx);

        
        object? result = await filter.InvokeAsync(ctx, DelegateBadRequest);

        
        result.Should().NotBeNull();
        result.Should().BeAssignableTo<IStatusCodeHttpResult>();
        ((IStatusCodeHttpResult)result!).StatusCode.Should().Be(425);
    }

    [Fact]
    public async Task EndpointFilter_SecondRequestWithCompletedKey_ReturnsCachedResult()
    {
        WebApplication app = BuildApp();
        IdempotencyEndpointFilter<TestObj> filter = app.Services.GetRequiredService<IdempotencyEndpointFilter<TestObj>>();
        IDistributedCache cache = app.Services.GetRequiredService<IDistributedCache>();
        JsonSerializerOptions jOpts = app.Services.GetRequiredService<JsonSerializerOptions>();

        HttpContext httpCtx = MakeHttpContext(RequestHeadersWithKey, ResponseHeaders);
        DefaultEndpointFilterInvocationContext ctx = new DefaultEndpointFilterInvocationContext(httpCtx);
        await filter.InvokeAsync(ctx, DelegateOkObject);

        string cacheKey = $"{CacheKeysPrefix}:{IdempotencyKey}";
        string? raw = await cache.GetStringAsync(cacheKey);
        JsonSerializer.Deserialize<ApiRequest>(raw!, jOpts)!.Status
            .Should().Be(IdempotencyRequestStatus.Completed);


        IdempotencyEndpointFilter<TestObj> filter2 = app.Services.GetRequiredService<IdempotencyEndpointFilter<TestObj>>();
        HttpContext httpCtx2 = MakeHttpContext(RequestHeadersWithKey, ResponseHeaders);
        DefaultEndpointFilterInvocationContext ctx2 = new DefaultEndpointFilterInvocationContext(httpCtx2);
        object? result2 = await filter2.InvokeAsync(ctx2, DelegateBadRequest);

        
        result2.Should().NotBeNull();
        result2.Should().BeOfType<Ok<TestObj>>();
        ((IStatusCodeHttpResult)result2!).StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task EndpointFilter_StatusTransition_InProgressBeforeHandlerCompleted()
    {
        WebApplication app = BuildApp();
        IdempotencyEndpointFilter<TestObj> filter = app.Services.GetRequiredService<IdempotencyEndpointFilter<TestObj>>();
        IDistributedCache cache = app.Services.GetRequiredService<IDistributedCache>();
        JsonSerializerOptions jOpts = app.Services.GetRequiredService<JsonSerializerOptions>();

        string cacheKey = $"{CacheKeysPrefix}:{IdempotencyKey}";
        ApiRequest? request = null;

        EndpointFilterDelegate slowDelegate = new EndpointFilterDelegate(async invCtx =>
        {
            string? raw = await cache.GetStringAsync(cacheKey);

            if (raw != null)
            {
                request = JsonSerializer.Deserialize<ApiRequest>(raw, jOpts);
            }
                
            await Task.Delay(10);
            return TypedResults.Ok(new TestObj { Id = 1 });
        });

        HttpContext httpCtx = MakeHttpContext(RequestHeadersWithKey, ResponseHeaders);
        DefaultEndpointFilterInvocationContext ctx = new DefaultEndpointFilterInvocationContext(httpCtx);

     
        await filter.InvokeAsync(ctx, slowDelegate);

    
        request.Should().NotBeNull();
        request!.Status.Should().Be(IdempotencyRequestStatus.InProgress);
    }


    //интеграционный тест
    [Fact]
    public async Task EndpointFilter_ConcurrentRequests_OnlyOneExecutesHandler()
    {
        WebApplication app = BuildApp();
        IDistributedCache cache = app.Services.GetRequiredService<IDistributedCache>();
        JsonSerializerOptions jOpts = app.Services.GetRequiredService<JsonSerializerOptions>();

        int handlerCallCount = 0;

        EndpointFilterDelegate countingDelegate = new EndpointFilterDelegate(async _ =>
        {
            System.Threading.Interlocked.Increment(ref handlerCallCount);
            await Task.Delay(50);
            return TypedResults.Ok(new TestObj { Id = handlerCallCount });
        });


        object?[] results = await Task.WhenAll(
            RunFilterAsync(app, countingDelegate),
            RunFilterAsync(app, countingDelegate)
        );


        int successCount = 0;
        int tooEarlyCount = 0;
        int cachedCount = 0;

        foreach (object? r in results)
        {
            if (r is IStatusCodeHttpResult result)
            {
                if (result.StatusCode == 425)
                {
                    tooEarlyCount++;
                }
                else if (result.StatusCode == 200)
                {
                    successCount++;
                    cachedCount++;
                }
            }
        }

        handlerCallCount.Should().BeLessOrEqualTo(1, "при параллельных запросах бизнес-логика должна выполняться не более одного раза");
        (tooEarlyCount + cachedCount).Should().BeGreaterOrEqualTo(1, "хотя бы один из параллельных запросов должен получить 425 или кешированный ответ");
    }

    private async Task<object?> RunFilterAsync(WebApplication app, EndpointFilterDelegate handler)
    {
        IdempotencyEndpointFilter<TestObj> filter = app.Services.GetRequiredService<IdempotencyEndpointFilter<TestObj>>();
        HttpContext httpCtx = MakeHttpContext(RequestHeadersWithKey, ResponseHeaders);
        DefaultEndpointFilterInvocationContext ctx = new DefaultEndpointFilterInvocationContext(httpCtx);
        return await filter.InvokeAsync(ctx, handler);
    }
}
