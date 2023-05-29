using System.IO;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace Delobytes.AspNetCore.Idempotency.Tests;

public class IdempotencyAttributeTests
{
    private static readonly string RequestMethod = "POST";
    private static readonly string RequestPath = "/json";
    private static readonly string RequestQueryString = "?pageSize=5";
    private static readonly Dictionary<string, StringValues> RequestHeadersWithKey = new Dictionary<string, StringValues>
            {
                { "RequestHeader1", "RequestHeader1Value" },
                { "Idempotency-Key", "c903b5ac-ce6d-47d5-aac0-1ddad0f308c9" },
            };
    private static readonly Dictionary<string, StringValues> RequestHeadersWoKey = new Dictionary<string, StringValues>
            {
                { "RequestHeader1", "RequestHeader1Value" },
            };
    private static readonly Dictionary<string, StringValues> ResponseHeaders = new Dictionary<string, StringValues>
            {
                { "ResponseHeader1", "ResponseHeader1Value" },
                { "Content-Type", "application/json" }
            };
    private static readonly string IdempotencyHeader = "Idempotency-Key";
    private static readonly string IdempotencyKey = "c903b5ac-ce6d-47d5-aac0-1ddad0f308c9";

    private static readonly Action<IdempotencyControlOptions> RegularOptions = options =>
    {
        options.Enabled = true;
        options.Optional = false;
        options.HeaderRequired = true;
        options.IdempotencyHeader = IdempotencyHeader;
    };


    #region Infrastructure
    private WebApplication CreateApplication(Action<IdempotencyControlOptions> options)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddIdempotencyControl(options);

        WebApplication app = builder.Build();

        return app;
    }

    private WebApplication CreateGoodCacheApplication(Action<IdempotencyControlOptions> options)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddIdempotencyControl(options);

        WebApplication app = builder.Build();

        return app;
    }

    private WebApplication CreateBadCacheApplication(Action<IdempotencyControlOptions> options)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        builder.Services.AddSingleton<IDistributedCache, BadDistributedCache>();
        builder.Services.AddIdempotencyControl(options);

        WebApplication app = builder.Build();

        return app;
    }

    private WebApplication CreateSlowCacheApplication(Action<IdempotencyControlOptions> options)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        builder.Services.AddSingleton<IDistributedCache, SlowDistributedCache>();
        builder.Services.AddIdempotencyControl(options);

        WebApplication app = builder.Build();

        return app;
    }

    private (ResourceExecutingContext ctx, ResourceExecutionDelegate delegat) GetContexts(HttpContext preCtx, HttpContext postCtx, IActionResult postCtxResult)
    {
        ActionContext preActionContext = new ActionContext(preCtx, new RouteData(), new ActionDescriptor());
        List<IFilterMetadata> filters = new List<IFilterMetadata>();
        List<IValueProviderFactory> values = new List<IValueProviderFactory>();

        ActionContext postActionContext = new ActionContext(postCtx, new RouteData(), new ActionDescriptor());
        ResourceExecutedContext nextContext = new ResourceExecutedContext(postActionContext, filters);
        nextContext.Result = postCtxResult;

        ResourceExecutingContext context = new ResourceExecutingContext(preActionContext, filters, values);
        ResourceExecutionDelegate next = new ResourceExecutionDelegate(() => Task.FromResult(nextContext));

        return (context, next);
    }

    private HttpContext GetHttpContextWithRequest(Dictionary<string, StringValues> requestHeaders, string requestBody = "{ \"request\": true }")
    {
        MockRepository mocks = new MockRepository(MockBehavior.Default);
        mocks.CallBase = true;

        FeatureCollection features = new FeatureCollection();

        Mock<IHttpRequestFeature> requestMock = new Mock<IHttpRequestFeature>();
        Mock<PipeReader> mockReqBodyReader = mocks.Create<PipeReader>();
        MemoryStream requestBodyMs = new MemoryStream();
        requestBodyMs.WriteAsync(Encoding.UTF8.GetBytes(requestBody));
        requestBodyMs.Seek(0, SeekOrigin.Begin);

        requestMock.Setup(h => h.Body).Returns(requestBodyMs);
        requestMock.Setup(h => h.Path).Returns(PathString.FromUriComponent(RequestPath));
        requestMock.Setup(h => h.RawTarget).Returns(PathString.FromUriComponent(RequestPath));
        requestMock.Setup(h => h.Protocol).Returns("HTTP/1.1");
        requestMock.Setup(h => h.Scheme).Returns("http");
        requestMock.Setup(h => h.Method).Returns(RequestMethod);
        requestMock.Setup(p => p.Headers).Returns(new HeaderDictionary(requestHeaders));
        requestMock.Setup(h => h.QueryString).Returns(RequestQueryString);
        features.Set(requestMock.Object);

        Mock<IHttpResponseFeature> responseMock = new Mock<IHttpResponseFeature>();
        responseMock.SetupProperty(x => x.StatusCode);
        features.Set(responseMock.Object);

        DefaultHttpContext context = new DefaultHttpContext(features);
        context.Request.Host = HostString.FromUriComponent("localhost:443");
        context.Request.ContentLength = requestBodyMs.Length;

        return context;
    }

    private HttpContext GetHttpContextWithRequestAndResponse(Dictionary<string, StringValues> requestHeaders,
        Dictionary<string, StringValues> responseHeaders, string requestBody = "{ \"request\": true }",
        string responseBody = "{ \"response\": true }", int responseStatusCode = 200)
    {
        MockRepository mocks = new MockRepository(MockBehavior.Default);
        mocks.CallBase = true;

        FeatureCollection features = new FeatureCollection();

        Mock<IHttpRequestFeature> requestMock = new Mock<IHttpRequestFeature>();
        Mock<PipeReader> mockReqBodyReader = mocks.Create<PipeReader>();
        MemoryStream requestBodyMs = new MemoryStream();
        requestBodyMs.WriteAsync(Encoding.UTF8.GetBytes(requestBody));
        requestBodyMs.Seek(0, SeekOrigin.Begin);

        requestMock.Setup(h => h.Body).Returns(requestBodyMs);
        requestMock.Setup(h => h.Path).Returns(PathString.FromUriComponent(RequestPath));
        requestMock.Setup(h => h.RawTarget).Returns(PathString.FromUriComponent(RequestPath));
        requestMock.Setup(h => h.Protocol).Returns("HTTP/1.1");
        requestMock.Setup(h => h.Scheme).Returns("http");
        requestMock.Setup(h => h.Method).Returns(RequestMethod);
        requestMock.Setup(p => p.Headers).Returns(new HeaderDictionary(requestHeaders));
        requestMock.Setup(h => h.QueryString).Returns(RequestQueryString);
        features.Set(requestMock.Object);

        Mock<IHttpResponseFeature> responseMock = new Mock<IHttpResponseFeature>();
        responseMock.SetupProperty(x => x.StatusCode);
        responseMock.Setup(p => p.Headers).Returns(new HeaderDictionary(responseHeaders));
        features.Set(responseMock.Object);

        Mock<IHttpResponseBodyFeature> responseBodyMock = new Mock<IHttpResponseBodyFeature>();
        MemoryStream responseBodyMs = new MemoryStream();
        responseBodyMs.WriteAsync(Encoding.UTF8.GetBytes(responseBody));
        responseBodyMs.Seek(0, SeekOrigin.Begin);
        responseBodyMock.Setup(o => o.Stream).Returns(responseBodyMs);
        features.Set(responseBodyMock.Object);

        Mock<IEndpointFeature> endpointMock = new Mock<IEndpointFeature>();
        endpointMock.SetupProperty(x => x.Endpoint);
        features.Set(endpointMock.Object);

        DefaultHttpContext context = new DefaultHttpContext(features);
        context.Request.Host = HostString.FromUriComponent("localhost:443");
        context.Request.ContentLength = requestBodyMs.Length;
        context.Response.StatusCode = responseStatusCode;
        
        return context;
    }
    #endregion


    [Fact]
    public void IdempotencyControl_InitializedSuccessfully()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        Action configureOptions = () =>
        {
            builder.Services.AddDistributedMemoryCache();
            builder.Services.AddIdempotencyControl(RegularOptions);
        };

        Exception ex = Record.Exception(configureOptions);

        ex.Should().BeNull();
    }

    [Fact]
    public async Task IdempotencyControl_IsTransparent_WhenDisabled()
    {
        Action<IdempotencyControlOptions> opt = options =>
        {
            options.Enabled = false;
            options.Optional = false;
            options.HeaderRequired = true;
            options.IdempotencyHeader = IdempotencyHeader;
        };

        WebApplication app = CreateApplication(opt);
        IdempotencyFilterAttribute attr = app.Services.GetRequiredService<IdempotencyFilterAttribute>();

        (ResourceExecutingContext ctx, ResourceExecutionDelegate delegat) = GetContexts(new DefaultHttpContext(), new DefaultHttpContext(), new OkResult());

        Func<Task> execute = async () =>
        {
            await attr.OnResourceExecutionAsync(ctx, delegat);
        };

        Exception ex = await Record.ExceptionAsync(execute);

        IActionResult? result = ctx.Result;

        ex.Should().BeNull();
        result.Should().BeNull();
    }

    [Fact]
    public async Task IdempotencyControl_Throws_OnCacheFailure_WhenNotOptional()
    {
        Action<IdempotencyControlOptions> opt = options =>
        {
            options.Enabled = true;
            options.Optional = false;
            options.HeaderRequired = true;
            options.IdempotencyHeader = IdempotencyHeader;
        };

        WebApplication app = CreateBadCacheApplication(opt);
        IdempotencyFilterAttribute attr = app.Services.GetRequiredService<IdempotencyFilterAttribute>();

        HttpContext preCtx = GetHttpContextWithRequest(RequestHeadersWithKey);
        HttpContext postCtx = GetHttpContextWithRequestAndResponse(RequestHeadersWithKey, ResponseHeaders);

        (ResourceExecutingContext ctx, ResourceExecutionDelegate delegat) = GetContexts(preCtx, postCtx, new OkResult());

        Func<Task> execute = async () =>
        {
            await attr.OnResourceExecutionAsync(ctx, delegat);
        };

        Exception ex = await Record.ExceptionAsync(execute);

        ex.Should().NotBeNull();
        ex.Should().BeOfType<IdempotencyException>();
    }

    [Fact]
    public async Task IdempotencyControl_NotThrows_OnCacheFailure_WhenOptional()
    {
        Action<IdempotencyControlOptions> opt = options =>
        {
            options.Enabled = true;
            options.Optional = true;
            options.HeaderRequired = true;
            options.IdempotencyHeader = IdempotencyHeader;
        };

        WebApplication app = CreateBadCacheApplication(opt);
        IdempotencyFilterAttribute attr = app.Services.GetRequiredService<IdempotencyFilterAttribute>();

        HttpContext preCtx = GetHttpContextWithRequest(RequestHeadersWithKey);
        HttpContext postCtx = GetHttpContextWithRequestAndResponse(RequestHeadersWithKey, ResponseHeaders);

        (ResourceExecutingContext ctx, ResourceExecutionDelegate delegat) = GetContexts(preCtx, postCtx, new OkResult());

        Func<Task> execute = async () =>
        {
            await attr.OnResourceExecutionAsync(ctx, delegat);
        };

        Exception ex = await Record.ExceptionAsync(execute);

        IActionResult? result = ctx.Result;

        ex.Should().BeNull();
        result.Should().BeNull();
    }

    [Fact]
    public async Task IdempotencyControl_WoHeader_Fails_WhenRequired()
    {
        Action<IdempotencyControlOptions> opt = options =>
        {
            options.Enabled = true;
            options.Optional = false;
            options.HeaderRequired = true;
            options.IdempotencyHeader = IdempotencyHeader;
        };

        WebApplication app = CreateApplication(opt);
        IdempotencyFilterAttribute attr = app.Services.GetRequiredService<IdempotencyFilterAttribute>();

        (ResourceExecutingContext ctx, ResourceExecutionDelegate delegat) = GetContexts(new DefaultHttpContext(), new DefaultHttpContext(), new OkResult());

        Func<Task> execute = async () =>
        {
            await attr.OnResourceExecutionAsync(ctx, delegat);
        };

        Exception ex = await Record.ExceptionAsync(execute);

        if (ctx.Result is not BadRequestObjectResult badResult)
        {
            throw new InvalidOperationException("Bad result is not found");
        }

        ex.Should().BeNull();
        badResult.StatusCode.Should().Be(400);
    }


    [Fact]
    public async Task IdempotencyControl_WoHeader_NotFails_WhenOptional()
    {
        Action<IdempotencyControlOptions> opt = options =>
        {
            options.Enabled = true;
            options.Optional = false;
            options.HeaderRequired = false;
            options.IdempotencyHeader = IdempotencyHeader;
        };

        WebApplication app = CreateApplication(opt);
        IdempotencyFilterAttribute attr = app.Services.GetRequiredService<IdempotencyFilterAttribute>();

        (ResourceExecutingContext ctx, ResourceExecutionDelegate delegat) = GetContexts(new DefaultHttpContext(), new DefaultHttpContext(), new OkResult());

        Func<Task> execute = async () =>
        {
            await attr.OnResourceExecutionAsync(ctx, delegat);
        };

        Exception ex = await Record.ExceptionAsync(execute);

        IActionResult? result = ctx.Result;

        ex.Should().BeNull();
        result.Should().BeNull();
    }

    [Fact]
    public async Task IdempotencyControl_Throws_OnCacheTimeout_WhenGoBeyondTimeout()
    {
        Action<IdempotencyControlOptions> opt = options =>
        {
            options.Enabled = true;
            options.Optional = false;
            options.HeaderRequired = true;
            options.IdempotencyHeader = IdempotencyHeader;
            options.CacheRequestTimeoutMs = 2000;
        };

        WebApplication app = CreateSlowCacheApplication(opt);
        IdempotencyFilterAttribute attr = app.Services.GetRequiredService<IdempotencyFilterAttribute>();

        HttpContext preCtx = GetHttpContextWithRequest(RequestHeadersWithKey);
        HttpContext postCtx = GetHttpContextWithRequestAndResponse(RequestHeadersWithKey, ResponseHeaders);

        (ResourceExecutingContext ctx, ResourceExecutionDelegate delegat) = GetContexts(preCtx, postCtx, new OkResult());

        Func<Task> execute = async () =>
        {
            await attr.OnResourceExecutionAsync(ctx, delegat);
        };

        Exception ex = await Record.ExceptionAsync(execute);

        ex.Should().NotBeNull();
        ex.Should().BeOfType<IdempotencyException>();
    }

    [Fact]
    public async Task IdempotencyControl_NotThrows_OnCacheTimeout_WhenGoBeyondTimeout()
    {
        Action<IdempotencyControlOptions> opt = options =>
        {
            options.Enabled = true;
            options.Optional = true;
            options.HeaderRequired = true;
            options.IdempotencyHeader = IdempotencyHeader;
            options.CacheRequestTimeoutMs = 2000;
        };

        WebApplication app = CreateSlowCacheApplication(opt);
        IdempotencyFilterAttribute attr = app.Services.GetRequiredService<IdempotencyFilterAttribute>();

        HttpContext preCtx = GetHttpContextWithRequest(RequestHeadersWithKey);
        HttpContext postCtx = GetHttpContextWithRequestAndResponse(RequestHeadersWithKey, ResponseHeaders);

        (ResourceExecutingContext ctx, ResourceExecutionDelegate delegat) = GetContexts(preCtx, postCtx, new OkResult());

        Func<Task> execute = async () =>
        {
            await attr.OnResourceExecutionAsync(ctx, delegat);
        };

        Exception ex = await Record.ExceptionAsync(execute);

        ex.Should().BeNull();
    }

    [Fact]
    public async Task IdempotencyControl_Runs_Transparently_WhenRequestIsNew()
    {
        WebApplication app = CreateGoodCacheApplication(RegularOptions);
        IdempotencyFilterAttribute attr = app.Services.GetRequiredService<IdempotencyFilterAttribute>();

        HttpContext preCtx = GetHttpContextWithRequest(RequestHeadersWithKey);
        HttpContext postCtx = GetHttpContextWithRequestAndResponse(RequestHeadersWithKey, ResponseHeaders);

        (ResourceExecutingContext ctx, ResourceExecutionDelegate delegat) = GetContexts(preCtx, postCtx, new OkObjectResult("a1"));

        Func<Task> execute = async () =>
        {
            await attr.OnResourceExecutionAsync(ctx, delegat);
        };

        Exception ex = await Record.ExceptionAsync(execute);

        ResourceExecutedContext requestResult = await delegat.Invoke();

        ex.Should().BeNull();
        requestResult.Should().NotBeNull();
        requestResult.Result.Should().NotBeNull();
        requestResult.Result.Should().BeOfType<OkObjectResult>();

        if (requestResult.Result is not OkObjectResult okResult)
        {
            throw new InvalidOperationException("Ok result not found");
        }
        okResult.StatusCode.Should().Be(200);
        okResult.Value.Should().Be("a1");
    }

    [Fact]
    public async Task IdempotencyControl_Runs_Transparently_WhenRequestIsNew_AndSystemtextSerializer()
    {
        Action<IdempotencyControlOptions> opt = options =>
        {
            options.Enabled = true;
            options.Optional = false;
            options.HeaderRequired = true;
            options.IdempotencyHeader = IdempotencyHeader;
            options.CacheRequestTimeoutMs = 2000;
            options.BodyOutputFormatterType = OutputFormatterType.SystemText;
        };

        WebApplication app = CreateGoodCacheApplication(opt);
        IdempotencyFilterAttribute attr = app.Services.GetRequiredService<IdempotencyFilterAttribute>();

        HttpContext preCtx = GetHttpContextWithRequest(RequestHeadersWithKey);
        HttpContext postCtx = GetHttpContextWithRequestAndResponse(RequestHeadersWithKey, ResponseHeaders);

        (ResourceExecutingContext ctx, ResourceExecutionDelegate delegat) = GetContexts(preCtx, postCtx, new OkObjectResult("a1"));

        Func<Task> execute = async () =>
        {
            await attr.OnResourceExecutionAsync(ctx, delegat);
        };

        Exception ex = await Record.ExceptionAsync(execute);

        ResourceExecutedContext requestResult = await delegat.Invoke();

        ex.Should().BeNull();
        requestResult.Should().NotBeNull();
        requestResult.Result.Should().NotBeNull();
        requestResult.Result.Should().BeOfType<OkObjectResult>();

        if (requestResult.Result is not OkObjectResult okResult)
        {
            throw new InvalidOperationException("Ok result not found");
        }
        okResult.StatusCode.Should().Be(200);
        okResult.Value.Should().Be("a1");
    }

    [Fact]
    public async Task IdempotencyControl_SavesToCacheWithPrefix_WhenRequestIsNew()
    {
        string cacheKeyPrefix = "my-prefix";

        Action<IdempotencyControlOptions> opt = options =>
        {
            options.Enabled = true;
            options.Optional = false;
            options.HeaderRequired = true;
            options.IdempotencyHeader = IdempotencyHeader;
            options.CacheKeysPrefix = cacheKeyPrefix;
        };

        WebApplication app = CreateGoodCacheApplication(opt);

        IdempotencyFilterAttribute attr = app.Services.GetRequiredService<IdempotencyFilterAttribute>();

        HttpContext preCtx = GetHttpContextWithRequest(RequestHeadersWithKey);
        HttpContext postCtx = GetHttpContextWithRequestAndResponse(RequestHeadersWithKey, ResponseHeaders);

        (ResourceExecutingContext ctx, ResourceExecutionDelegate delegat) = GetContexts(preCtx, postCtx, new OkObjectResult("a1"));

        Func<Task> execute = async () =>
        {
            await attr.OnResourceExecutionAsync(ctx, delegat);
        };

        Exception ex = await Record.ExceptionAsync(execute);

        IDistributedCache cache = app.Services.GetRequiredService<IDistributedCache>();

        string cacheKey = $"{cacheKeyPrefix}:{IdempotencyKey}";
        string? cachedApiRequest = await cache.GetStringAsync(cacheKey);

        cachedApiRequest.Should().NotBeNullOrEmpty();

        JsonSerializerOptions serializerOptions = app.Services.GetRequiredService<JsonSerializerOptions>();        
        ApiRequest? requestFromCache = JsonSerializer.Deserialize<ApiRequest>(cachedApiRequest!, serializerOptions);

        requestFromCache.Should().NotBeNull();
        requestFromCache!.ApiRequestID.Should().Be(IdempotencyKey);
        requestFromCache!.Body.Should().NotBeNull();
        requestFromCache!.BodyType.Should().NotBeNullOrEmpty();
        requestFromCache!.Body.Should().NotBeNull();
        requestFromCache!.Headers.Should().NotBeNull();
        requestFromCache!.Method.Should().NotBeNullOrEmpty().And.Be(RequestMethod);
        requestFromCache!.Path.Should().NotBeNullOrEmpty().And.Be(RequestPath);
        requestFromCache!.Query.Should().NotBeNullOrEmpty().And.Be(RequestQueryString);
        requestFromCache!.ResultType.Should().NotBeNullOrEmpty();
        requestFromCache!.StatusCode.Should().NotBeNull().And.Be(200);
    }

    [Fact]
    public async Task IdempotencyControl_ReturnFromCache_WhenRequestIsKnown_WithOkResult()
    {
        WebApplication app = CreateGoodCacheApplication(RegularOptions);
        IdempotencyFilterAttribute attr = app.Services.GetRequiredService<IdempotencyFilterAttribute>();

        HttpContext preCtx = GetHttpContextWithRequest(RequestHeadersWithKey);
        HttpContext postCtx = GetHttpContextWithRequestAndResponse(RequestHeadersWithKey, ResponseHeaders, responseStatusCode: 202);

        (ResourceExecutingContext ctx, ResourceExecutionDelegate delegat) = GetContexts(preCtx, postCtx, new OkResult());

        Func<Task> execute = async () =>
        {
            await attr.OnResourceExecutionAsync(ctx, delegat);
        };

        Exception ex = await Record.ExceptionAsync(execute);

        ResourceExecutedContext firstRequestResult = await delegat.Invoke();


        IdempotencyFilterAttribute attr2 = app.Services.GetRequiredService<IdempotencyFilterAttribute>();

        HttpContext preCtx2 = GetHttpContextWithRequestAndResponse(RequestHeadersWithKey, ResponseHeaders);
        HttpContext postCtx2 = GetHttpContextWithRequestAndResponse(RequestHeadersWithKey, ResponseHeaders);

        (ResourceExecutingContext ctx2, ResourceExecutionDelegate delegat2) = GetContexts(preCtx2, postCtx2, new BadRequestResult());

        Func<Task> executeRequest2 = async () =>
        {
            await attr2.OnResourceExecutionAsync(ctx2, delegat2);
        };

        Exception ex2 = await Record.ExceptionAsync(executeRequest2);

        ex2.Should().BeNull();
        ctx2.Result.Should().NotBeNull();
        ctx2.Result.Should().BeOfType<StatusCodeResult>();

        if (ctx2.Result is not StatusCodeResult statusCodeResult)
        {
            throw new InvalidOperationException("StatucScode result not found");
        }
        statusCodeResult.StatusCode.Should().Be(202);
    }

    [Fact]
    public async Task IdempotencyControl_ReturnFromCache_WhenRequestIsKnown_WithOkObjectResult()
    {
        WebApplication app = CreateGoodCacheApplication(RegularOptions);
        IdempotencyFilterAttribute attr = app.Services.GetRequiredService<IdempotencyFilterAttribute>();

        HttpContext preCtx = GetHttpContextWithRequest(RequestHeadersWithKey);
        HttpContext postCtx = GetHttpContextWithRequestAndResponse(RequestHeadersWithKey, ResponseHeaders);

        (ResourceExecutingContext ctx, ResourceExecutionDelegate delegat) = GetContexts(preCtx, postCtx, new OkObjectResult("a1"));

        Func<Task> execute = async () =>
        {
            await attr.OnResourceExecutionAsync(ctx, delegat);
        };

        Exception ex = await Record.ExceptionAsync(execute);

        ResourceExecutedContext firstRequestResult = await delegat.Invoke();


        IdempotencyFilterAttribute attr2 = app.Services.GetRequiredService<IdempotencyFilterAttribute>();

        HttpContext preCtx2 = GetHttpContextWithRequestAndResponse(RequestHeadersWithKey, ResponseHeaders);
        HttpContext postCtx2 = GetHttpContextWithRequestAndResponse(RequestHeadersWithKey, ResponseHeaders);

        (ResourceExecutingContext ctx2, ResourceExecutionDelegate delegat2) = GetContexts(preCtx2, postCtx2, new BadRequestResult());

        Func<Task> executeRequest2 = async () =>
        {
            await attr2.OnResourceExecutionAsync(ctx2, delegat2);
        };

        Exception ex2 = await Record.ExceptionAsync(executeRequest2);

        ex2.Should().BeNull();
        ctx2.Result.Should().NotBeNull();
        ctx2.Result.Should().BeOfType<ObjectResult>();

        if (ctx2.Result is not ObjectResult objResult)
        {
            throw new InvalidOperationException("Object result not found");
        }
        objResult.StatusCode.Should().Be(200);
        objResult.Value.Should().Be("a1");
    }

    [Fact]
    public async Task IdempotencyControl_ReturnFromCache_WhenRequestIsKnown_WithCreatedAtResult()
    {
        WebApplication app = CreateGoodCacheApplication(RegularOptions);
        IdempotencyFilterAttribute attr = app.Services.GetRequiredService<IdempotencyFilterAttribute>();

        HttpContext preCtx = GetHttpContextWithRequest(RequestHeadersWithKey);
        HttpContext postCtx = GetHttpContextWithRequestAndResponse(RequestHeadersWithKey, ResponseHeaders, responseStatusCode: 201);

        Guid instanceId = Guid.NewGuid();
        string routeName = "InstancesGetInstance";
        object routeValues = new { InstanceID = instanceId };
        CreatedAtRouteResult createdAtResult = new CreatedAtRouteResult(routeName, routeValues, "a1");

        (ResourceExecutingContext ctx, ResourceExecutionDelegate delegat) = GetContexts(preCtx, postCtx, createdAtResult);

        Func<Task> execute = async () =>
        {
            await attr.OnResourceExecutionAsync(ctx, delegat);
        };

        Exception ex = await Record.ExceptionAsync(execute);

        ResourceExecutedContext firstRequestResult = await delegat.Invoke();


        IdempotencyFilterAttribute attr2 = app.Services.GetRequiredService<IdempotencyFilterAttribute>();

        HttpContext preCtx2 = GetHttpContextWithRequestAndResponse(RequestHeadersWithKey, ResponseHeaders);
        HttpContext postCtx2 = GetHttpContextWithRequestAndResponse(RequestHeadersWithKey, ResponseHeaders);

        (ResourceExecutingContext ctx2, ResourceExecutionDelegate delegat2) = GetContexts(preCtx2, postCtx2, new BadRequestResult());

        Func<Task> executeRequest2 = async () =>
        {
            await attr2.OnResourceExecutionAsync(ctx2, delegat2);
        };

        Exception ex2 = await Record.ExceptionAsync(executeRequest2);

        ex2.Should().BeNull();
        ctx2.Result.Should().NotBeNull();
        ctx2.Result.Should().BeOfType<CreatedAtRouteResult>();

        if (ctx2.Result is not CreatedAtRouteResult createdResult)
        {
            throw new InvalidOperationException("CreatedAt result not found");
        }
        createdResult.StatusCode.Should().Be(201);
        createdResult.Value.Should().Be("a1");
        createdResult.RouteName.Should().Be(routeName);
        createdAtResult.RouteValues.Should().ContainSingle().And.ContainValue(instanceId);
    }
}
