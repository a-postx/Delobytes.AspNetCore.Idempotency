using System;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
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

namespace Delobytes.AspNetCore.Idempotency.Tests;

public class IdempotencyEndpointFilterTests
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

    private static readonly Action<IdempotencyControlOptions> DefaultOptions = options =>
    {
        options.Enabled = true;
        options.Optional = false;
        options.HeaderRequired = true;
        options.IdempotencyHeader = IdempotencyHeader;
    };
    private static readonly EndpointFilterDelegate DefaultOkResult = new EndpointFilterDelegate(async invocationContext =>
    {
        await Task.Delay(10);
        return TypedResults.Ok();
    });
    private static readonly EndpointFilterDelegate DefaultOkObjectResult = new EndpointFilterDelegate(async invocationContext =>
    {
        await Task.Delay(10);
        return TypedResults.Ok(_testObj);
    });
    private static readonly EndpointFilterDelegate DefaultBadRequestResult = new EndpointFilterDelegate(async invocationContext =>
    {
        await Task.Delay(10);
        return TypedResults.BadRequest("ooo!");
    });

    private static TestObj _testObj = new TestObj { Id = 1 };


    #region Infrastructure
    private WebApplication CreateApplication<T>(Action<IdempotencyControlOptions> options) where T : class
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        builder.Services.AddDistributedMemoryCache();        
        builder.Services.AddIdempotencyControl(options);
        builder.Services.AddScoped<IdempotencyEndpointFilter<T>>();

        WebApplication app = builder.Build();

        return app;
    }

    private WebApplication CreateSlowCacheApplication(Action<IdempotencyControlOptions> options)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        builder.Services.AddSingleton<IDistributedCache, SlowDistributedCache>();
        builder.Services.AddIdempotencyControl(options);
        builder.Services.AddScoped<IdempotencyEndpointFilter<TestObj>>();

        WebApplication app = builder.Build();

        return app;
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
    public void Filter_RegisteredSuccessfully()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        Action configureOptions = () =>
        {
            builder.Services.AddDistributedMemoryCache();
            builder.Services.AddIdempotencyControl(DefaultOptions);
        };

        Exception ex = Record.Exception(configureOptions);

        ex.Should().BeNull();

        WebApplication app = CreateApplication<TestObj>(DefaultOptions);
        IdempotencyEndpointFilter<TestObj> filter = app.Services.GetRequiredService<IdempotencyEndpointFilter<TestObj>>();

        Assert.NotNull(filter);
    }

    [Fact]
    public async Task Filter_IsTransparent_WhenDisabled()
    {
        Action<IdempotencyControlOptions> opt = options =>
        {
            options.Enabled = false;
            options.Optional = false;
            options.HeaderRequired = true;
            options.IdempotencyHeader = IdempotencyHeader;
        };

        WebApplication app = CreateApplication<TestObj>(opt);
        IdempotencyEndpointFilter<TestObj> filter = app.Services.GetRequiredService<IdempotencyEndpointFilter<TestObj>>();
        HttpContext preCtx = GetHttpContextWithRequest(RequestHeadersWoKey);
        DefaultEndpointFilterInvocationContext ctx = new DefaultEndpointFilterInvocationContext(preCtx);

        object? response = null;

        Func<Task> execute = async () =>
        {
            response = await filter.InvokeAsync(ctx, DefaultOkObjectResult);
        };

        Exception ex = await Record.ExceptionAsync(execute);

        ex.Should().BeNull();
        response.As<Ok<TestObj>>().Should().NotBeNull();
    }


    [Fact]
    public async Task IdempotencyControl_WoHeader_Fails_WhenRequired()
    {
        WebApplication app = CreateApplication<TestObj>(DefaultOptions);
        IdempotencyEndpointFilter<TestObj> filter = app.Services.GetRequiredService<IdempotencyEndpointFilter<TestObj>>();
        HttpContext preCtx = GetHttpContextWithRequest(RequestHeadersWoKey);
        DefaultEndpointFilterInvocationContext ctx = new DefaultEndpointFilterInvocationContext(preCtx);

        var result = await filter.InvokeAsync(ctx, DefaultOkObjectResult);

        result.Should().NotBeNull();
        result.As<BadRequest<string>>().Should().NotBeNull();
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

        WebApplication app = CreateApplication<TestObj>(opt);
        IdempotencyEndpointFilter<TestObj> filter = app.Services.GetRequiredService<IdempotencyEndpointFilter<TestObj>>();
        HttpContext preCtx = GetHttpContextWithRequest(RequestHeadersWoKey);
        DefaultEndpointFilterInvocationContext ctx = new DefaultEndpointFilterInvocationContext(preCtx);

        object? response = null;

        Func<Task> execute = async () =>
        {
            response = await filter.InvokeAsync(ctx, DefaultOkObjectResult);
        };

        Exception ex = await Record.ExceptionAsync(execute);

        ex.Should().BeNull();
        response.As<Ok<TestObj>>().Should().NotBeNull();
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
        IdempotencyEndpointFilter<TestObj> filter = app.Services.GetRequiredService<IdempotencyEndpointFilter<TestObj>>();

        //HttpContext preCtx = GetHttpContextWithRequest(RequestHeadersWithKey);
        HttpContext postCtx = GetHttpContextWithRequestAndResponse(RequestHeadersWithKey, ResponseHeaders);
        DefaultEndpointFilterInvocationContext ctx = new DefaultEndpointFilterInvocationContext(postCtx);

        object? response = null;

        Func<Task> execute = async () =>
        {
            response = await filter.InvokeAsync(ctx, DefaultOkObjectResult);
        };

        Exception ex = await Record.ExceptionAsync(execute);

        ex.Should().NotBeNull();
        ex.Should().BeOfType<IdempotencyException>();
    }

    [Fact]
    public async Task IdempotencyControl_Runs_Transparently_WhenRequestIsNew()
    {
        WebApplication app = CreateApplication<TestObj>(DefaultOptions);
        IdempotencyEndpointFilter<TestObj> filter = app.Services.GetRequiredService<IdempotencyEndpointFilter<TestObj>>();

        //HttpContext preCtx = GetHttpContextWithRequest(RequestHeadersWithKey);
        HttpContext postCtx = GetHttpContextWithRequestAndResponse(RequestHeadersWithKey, ResponseHeaders);
        DefaultEndpointFilterInvocationContext ctx = new DefaultEndpointFilterInvocationContext(postCtx);

        object? response = null;

        Func<Task> execute = async () =>
        {
            response = await filter.InvokeAsync(ctx, DefaultOkObjectResult);
        };

        Exception ex = await Record.ExceptionAsync(execute);

        ex.Should().BeNull();
        response.Should().NotBeNull();

        Ok<TestObj> result = response.As<Ok<TestObj>>();

        result.Should().NotBeNull();
        result.StatusCode.Should().Be(200);
        result.Value.Should().Be(_testObj);
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

        WebApplication app = CreateApplication<TestObj>(opt);
        IdempotencyEndpointFilter<TestObj> filter = app.Services.GetRequiredService<IdempotencyEndpointFilter<TestObj>>();

        //HttpContext preCtx = GetHttpContextWithRequest(RequestHeadersWithKey);
        HttpContext postCtx = GetHttpContextWithRequestAndResponse(RequestHeadersWithKey, ResponseHeaders);
        DefaultEndpointFilterInvocationContext ctx = new DefaultEndpointFilterInvocationContext(postCtx);

        object? response = null;

        Func<Task> execute = async () =>
        {
            response = await filter.InvokeAsync(ctx, DefaultOkObjectResult);
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
        WebApplication app = CreateApplication<TestObj>(DefaultOptions);
        IdempotencyEndpointFilter<TestObj> filter = app.Services.GetRequiredService<IdempotencyEndpointFilter<TestObj>>();
        HttpContext postCtx = GetHttpContextWithRequestAndResponse(RequestHeadersWithKey, ResponseHeaders);
        DefaultEndpointFilterInvocationContext ctx = new DefaultEndpointFilterInvocationContext(postCtx);

        object? firstResponse = null;

        Func<Task> execute = async () =>
        {
            firstResponse = await filter.InvokeAsync(ctx, DefaultOkObjectResult);
        };

        Exception ex = await Record.ExceptionAsync(execute);

        ex.Should().BeNull();
        firstResponse.Should().NotBeNull();

        Ok<TestObj> firstResult = firstResponse.As<Ok<TestObj>>();
        firstResult.StatusCode.Should().Be(200);

        IdempotencyEndpointFilter<TestObj> filter2 = app.Services.GetRequiredService<IdempotencyEndpointFilter<TestObj>>();
        HttpContext postCtx2 = GetHttpContextWithRequestAndResponse(RequestHeadersWithKey, ResponseHeaders);
        DefaultEndpointFilterInvocationContext ctx2 = new DefaultEndpointFilterInvocationContext(postCtx2);

        object? secondResponse = null;

        Func<Task> execute2 = async () =>
        {
            secondResponse = await filter2.InvokeAsync(ctx2, DefaultBadRequestResult);
        };

        Exception ex2 = await Record.ExceptionAsync(execute2);

        ex2.Should().BeNull();
        secondResponse.Should().NotBeNull();
        secondResponse.Should().BeOfType<Ok<TestObj>>();

        if (secondResponse is IStatusCodeHttpResult scResult)
        {
            scResult.StatusCode.Should().Be(200);
        }
    }

    [Fact]
    public async Task IdempotencyControl_ReturnFromCache_WhenRequestIsKnown_WithOkObjectResult()
    {
        WebApplication app = CreateApplication<TestObj>(DefaultOptions);
        IdempotencyEndpointFilter<TestObj> filter = app.Services.GetRequiredService<IdempotencyEndpointFilter<TestObj>>();
        HttpContext postCtx = GetHttpContextWithRequestAndResponse(RequestHeadersWithKey, ResponseHeaders);
        DefaultEndpointFilterInvocationContext ctx = new DefaultEndpointFilterInvocationContext(postCtx);

        object? firstResponse = null;

        Func<Task> execute = async () =>
        {
            firstResponse = await filter.InvokeAsync(ctx, DefaultOkObjectResult);
        };

        Exception ex = await Record.ExceptionAsync(execute);

        ex.Should().BeNull();
        firstResponse.Should().NotBeNull();

        Ok<TestObj> firstResult = firstResponse.As<Ok<TestObj>>();
        firstResult.StatusCode.Should().Be(200);

        IdempotencyEndpointFilter<TestObj> filter2 = app.Services.GetRequiredService<IdempotencyEndpointFilter<TestObj>>();
        HttpContext postCtx2 = GetHttpContextWithRequestAndResponse(RequestHeadersWithKey, ResponseHeaders);
        DefaultEndpointFilterInvocationContext ctx2 = new DefaultEndpointFilterInvocationContext(postCtx2);

        object? secondResponse = null;

        Func<Task> execute2 = async () =>
        {
            secondResponse = await filter2.InvokeAsync(ctx2, DefaultBadRequestResult);
        };

        Exception ex2 = await Record.ExceptionAsync(execute2);

        ex2.Should().BeNull();
        secondResponse.Should().NotBeNull();
        secondResponse.Should().BeOfType<Ok<TestObj>>();

        if (secondResponse is IStatusCodeHttpResult scResult)
        {
            scResult.StatusCode.Should().Be(200);
        }

        TestObj? value = secondResponse.As<Ok<TestObj>>().Value;
        value.Should().NotBeNull();
        value!.Id.Should().Be(_testObj.Id);
    }

    [Fact]
    public async Task IdempotencyControl_ReturnFromCache_WhenRequestIsKnown_WithCreatedAtResult()
    {
        WebApplication app = CreateApplication<string>(DefaultOptions);
        IdempotencyEndpointFilter<string> filter = app.Services.GetRequiredService<IdempotencyEndpointFilter<string>>();
        HttpContext postCtx = GetHttpContextWithRequestAndResponse(RequestHeadersWithKey, ResponseHeaders);
        DefaultEndpointFilterInvocationContext ctx = new DefaultEndpointFilterInvocationContext(postCtx);

        Guid instanceId = Guid.NewGuid();
        string routeName = "InstancesGetInstance";
        object routeValues = new { InstanceID = instanceId };
        string value = "a1";
        CreatedAtRoute<string> createdAtResult = TypedResults.CreatedAtRoute(value, routeName, routeValues);

        EndpointFilterDelegate endpointDelegate = new EndpointFilterDelegate(async invocationContext =>
        {
            await Task.Delay(10);
            return createdAtResult;
        });

        object? firstResponse = null;

        Func<Task> execute = async () =>
        {
            firstResponse = await filter.InvokeAsync(ctx, endpointDelegate);
        };

        Exception ex = await Record.ExceptionAsync(execute);

        ex.Should().BeNull();
        firstResponse.Should().NotBeNull();

        IdempotencyEndpointFilter<string> filter2 = app.Services.GetRequiredService<IdempotencyEndpointFilter<string>>();
        HttpContext postCtx2 = GetHttpContextWithRequestAndResponse(RequestHeadersWithKey, ResponseHeaders);
        DefaultEndpointFilterInvocationContext ctx2 = new DefaultEndpointFilterInvocationContext(postCtx2);

        object? secondResponse = null;

        Func<Task> execute2 = async () =>
        {
            secondResponse = await filter2.InvokeAsync(ctx2, DefaultBadRequestResult);
        };

        Exception ex2 = await Record.ExceptionAsync(execute2);

        ex2.Should().BeNull();
        secondResponse.Should().NotBeNull();
        secondResponse.Should().BeOfType<CreatedAtRoute<string>>();

        if (secondResponse is IStatusCodeHttpResult scResult)
        {
            scResult.StatusCode.Should().Be(201);
        }

        if (secondResponse is not CreatedAtRoute<string> createdResult)
        {
            throw new InvalidOperationException("CreatedAt result not found");
        }
        createdResult.StatusCode.Should().Be(201);
        createdResult.Value.Should().Be(value);
        createdResult.RouteName.Should().Be(routeName);
        createdAtResult.RouteValues.Should().ContainSingle().And.ContainValue(instanceId);
    }



    private class TestObj
    {
        public int Id { get; set; }
    }
}
